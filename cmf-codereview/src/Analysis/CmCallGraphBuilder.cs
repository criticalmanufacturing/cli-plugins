using Cmf.Cli.Plugin.CodeReview.Models;
using Cmf.Cli.Plugin.CodeReview.Enums;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cmf.Cli.Plugin.CodeReview;

/// <summary>
/// Builds a name-based method call graph across the "Both" scope: for every
/// <c>.MethodName(...)</c> call site (an invocation through member access) whose name matches
/// a REAL method defined somewhere in scope, records the caller, the call site, and whether that
/// call site is itself inside a loop. Local functions are never callable targets (see below), and
/// a name defined by more than one type is treated as ambiguous and gets no edges.
/// </summary>
public static class CmCallGraphBuilder
{
    public static MethodCallGraph BuildCallGraph(string repositoryRoot, CodeReviewFileCache? fileCache = null)
    {
        fileCache ??= new CodeReviewFileCache();
        var files = CodeReviewPaths.GetFiles(repositoryRoot, CodeReviewPaths.ResolveRoots(repositoryRoot, ReviewScope.Both));
        var graph = new MethodCallGraph();

        // Origin tracking: which distinct TYPES define each method name, considering only real
        // method declarations. Local functions are deliberately excluded from the callable-target
        // set: a member-access call (`receiver.Method()` -- the only call shape this graph records)
        // can never resolve to a local function, since a local function is invoked by bare name
        // within its enclosing member. Including them let a platform call such as
        // `_exceptionOrchestration.OpenProtocolInstance(...)` misbind to a same-named DEE-local
        // `OpenProtocolInstance`, fabricating a caller-loop chain. Partial classes share a type
        // name so count once; overloads within one type count once. A name defined by 2+ types is
        // ambiguous -- a name-based graph can't know which one a call site means, so it gets no
        // edges at all (missing a real cross-method loop is the accepted cost of never fabricating one).
        var definingTypesByMethod = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        // Per-file scan is independent (each file's own syntax tree); only the merge into the shared
        // dictionary needs to happen sequentially. Order doesn't matter here since each method name's
        // value is a HashSet of defining types, not an ordered list.
        var perFileMethodDeclarations = files
            .AsParallel()
            .Select(file => fileCache.Get(file).Root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Select(method => (
                    MethodName: method.Identifier.Text,
                    TypeName: method.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.Text ?? ""))
                .ToList())
            .ToList();

        foreach (var declarations in perFileMethodDeclarations)
        {
            foreach (var (methodName, typeName) in declarations)
            {
                if (!definingTypesByMethod.TryGetValue(methodName, out var types))
                {
                    types = new HashSet<string>(StringComparer.Ordinal);
                    definingTypesByMethod[methodName] = types;
                }
                types.Add(typeName);
            }
        }

        foreach (var (methodName, types) in definingTypesByMethod)
        {
            graph.DefinedMethodNames.Add(methodName);
            if (types.Count > 1)
            {
                graph.AmbiguousMethodNames.Add(methodName);
            }
        }

        // Same independence as above: each file's call sites are found from its own tree/loop state/method
        // ranges, so the scan runs per-file in parallel; only the append into the shared ordered lists
        // needs to happen sequentially, in file order, to match the original scan's deterministic output.
        var perFileCallSites = files
            .AsParallel().AsOrdered()
            .Select(file =>
            {
                var source = fileCache.Get(file);
                // Only loop membership (IsInLoop below) is read here, but the cached full loop state
                // is shared with the main rule pass, so nothing is computed twice overall.
                var loopState = source.LoopState;
                var methodRanges = source.MethodRanges;
                var callerRelativePath = CodeReviewPaths.GetRelativePath(repositoryRoot, file);

                var callSites = new List<(string CalledMethod, CallerRecord Record)>();
                foreach (var invocation in source.Root.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
                    {
                        continue;
                    }

                    var calledMethod = memberAccess.Name.Identifier.Text;
                    if (!graph.DefinedMethodNames.Contains(calledMethod) || graph.AmbiguousMethodNames.Contains(calledMethod))
                    {
                        continue;
                    }

                    var lineNumber = CodeReviewSyntaxTree.StartLine(invocation);
                    var callerMethodName = CmMethodAnalyzer.GetMethodNameForLine(methodRanges, lineNumber);
                    if (callerMethodName is null || callerMethodName == calledMethod)
                    {
                        continue;
                    }

                    callSites.Add((calledMethod, new CallerRecord
                    {
                        CallerMethodName = callerMethodName,
                        CallerRelativePath = callerRelativePath,
                        CallerLine = lineNumber,
                        IsInLoop = loopState.Lines.Contains(lineNumber)
                    }));
                }

                return callSites;
            })
            .ToList();

        foreach (var callSites in perFileCallSites)
        {
            foreach (var (calledMethod, record) in callSites)
            {
                if (!graph.CallersByMethod.TryGetValue(calledMethod, out var list))
                {
                    list = new List<CallerRecord>();
                    graph.CallersByMethod[calledMethod] = list;
                }

                list.Add(record);
            }
        }

        return graph;
    }
}
