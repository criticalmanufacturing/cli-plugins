using System.Text.RegularExpressions;
using Cmf.Cli.Plugin.CodeReview.Models;
using Cmf.Cli.Plugin.CodeReview.Enums;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cmf.Cli.Plugin.CodeReview;

public class ConstantStringMap
{
    public Dictionary<string, string> Global { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, Dictionary<string, string>> PerFile { get; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Detects DEE action classes (inheriting DeeDevBase) and how they call, or are called by,
/// Orchestration/other-DEE code, mirroring the DEE/Orchestration cross-reference heuristics --
/// invocations are located via the C# syntax tree, so a call is never confused with a comment,
/// a string literal, or split incorrectly across a line wrap.
/// </summary>
public static class CmDeeAnalyzer
{
    private static readonly Regex LiteralStringRegex = new(@"^""(?<val>[^""]*)""$", RegexOptions.Compiled);
    private static readonly Regex IdentifierRegex = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    /// <summary>
    /// The DEE-action name argument of a call the DEE dispatch recognizes: the first argument of
    /// <c>ExecuteDeeAction(name, ...)</c> (2+ args) or the single argument of an <c>x.Load(name)</c>
    /// entity load. Null when the invocation is neither shape. Inspects the syntax node directly
    /// rather than rendering it to text and regex-scanning, which (a) avoids allocating and matching
    /// a string per invocation across the whole repo, and (b) no longer double-counts when an outer
    /// invocation's rendered text merely contains a nested <c>.Load(...)</c>.
    /// </summary>
    private static string? GetDeeActionArgument(InvocationExpressionSyntax invocation)
    {
        var name = CodeReviewSyntaxTree.GetMemberName(invocation.Expression)?.Identifier.Text
                   ?? (invocation.Expression as IdentifierNameSyntax)?.Identifier.Text;
        var arguments = invocation.ArgumentList.Arguments;

        // ExecuteDeeAction(deeName, input, ...): dee name is the first argument. The old regex
        // required a following comma, i.e. at least two arguments.
        if (name == "ExecuteDeeAction" && arguments.Count >= 2)
        {
            return arguments[0].Expression.ToString();
        }

        // receiver.Load(deeName): an entity load of an Action by name. The old regex only matched a
        // member-access .Load with exactly one argument.
        if (name == "Load" && arguments.Count == 1 && invocation.Expression is MemberAccessExpressionSyntax)
        {
            return arguments[0].Expression.ToString();
        }

        return null;
    }

    /// <summary>
    /// The method name of an Orchestration call (<c>somethingOrchestration.Method(...)</c>), or null
    /// when the invocation isn't one. The receiver's trailing identifier must end in "orchestration"
    /// (case-insensitive on that O) -- the proxy field/property name varies per project
    /// (_orchestration, _projectOrchestration, this.ContainerOrchestration, ...). Direct syntax
    /// inspection, replacing a per-invocation ToString()+regex.
    /// </summary>
    private static string? GetOrchestrationCallMethod(InvocationExpressionSyntax invocation)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
        {
            return null;
        }

        var receiverName = memberAccess.Expression switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.Text,
            MemberAccessExpressionSyntax receiverAccess => receiverAccess.Name.Identifier.Text,
            _ => null
        };

        return receiverName is not null && EndsWithOrchestration(receiverName)
            ? memberAccess.Name.Identifier.Text
            : null;
    }

    private static bool EndsWithOrchestration(string identifier) =>
        identifier.EndsWith("orchestration", StringComparison.Ordinal) ||
        identifier.EndsWith("Orchestration", StringComparison.Ordinal);

    /// <summary>Finds every class under the DEE roots that inherits <c>DeeDevBase</c>, by name and file.</summary>
    public static DeeClassInfo GetDeeClassNames(string repositoryRoot, CodeReviewFileCache? fileCache = null)
    {
        fileCache ??= new CodeReviewFileCache();
        var info = new DeeClassInfo();
        var files = CodeReviewPaths.GetFiles(repositoryRoot, CodeReviewPaths.ResolveRoots(repositoryRoot, ReviewScope.DEEs));

        foreach (var file in files)
        {
            var root = fileCache.Get(file).Root;

            var deeClass = root.DescendantNodes()
                .OfType<ClassDeclarationSyntax>()
                .FirstOrDefault(c => InheritsFrom(c, "DeeDevBase"));

            if (deeClass is not null)
            {
                var name = deeClass.Identifier.Text;
                info.Names.Add(name);
                info.FileByName[name] = file;
            }
        }

        return info;
    }

    /// <summary>
    /// Combines <see cref="GetDeeClassNames"/> and <see cref="GetOrchestrationCallSitesInDees"/> into a
    /// single per-file syntax-tree walk over the DEEs scope -- callers needing both (as
    /// <c>CmFrameworkScanner</c> always does) should call this directly instead of the two single-purpose
    /// methods back to back, which would otherwise walk every DEE-scope file twice for no reason (same
    /// class of redundant-walk cost as <c>MasterDataActionGroupResolver.CollectDeclaredNames</c>).
    /// </summary>
    public static (DeeClassInfo DeeClassInfo, Dictionary<string, OrchestrationCallSite> OrchestrationCallSites) GetDeeClassInfoAndOrchestrationCallSites(
        string repositoryRoot, CodeReviewFileCache? fileCache = null)
    {
        fileCache ??= new CodeReviewFileCache();
        var files = CodeReviewPaths.GetFiles(repositoryRoot, CodeReviewPaths.ResolveRoots(repositoryRoot, ReviewScope.DEEs));

        // Parallel per-file scan, AsOrdered + sequential merge below, so results match exactly what the
        // two original sequential/ordered-parallel methods would have produced on their own (first
        // DeeDevBase class per file, first caller per Orchestration method).
        var perFile = files
            .AsParallel().AsOrdered()
            .Select(file =>
            {
                var root = fileCache.Get(file).Root;
                var callerRelativePath = CodeReviewPaths.GetRelativePath(repositoryRoot, file);
                string? deeClassName = null;
                var orchestrationHits = new List<(string Method, OrchestrationCallSite Site)>();

                foreach (var node in root.DescendantNodes())
                {
                    switch (node)
                    {
                        case ClassDeclarationSyntax classDeclaration when deeClassName is null && InheritsFrom(classDeclaration, "DeeDevBase"):
                            deeClassName = classDeclaration.Identifier.Text;
                            break;
                        case InvocationExpressionSyntax invocation:
                            var method = GetOrchestrationCallMethod(invocation);
                            if (method is not null)
                            {
                                orchestrationHits.Add((method, new OrchestrationCallSite
                                {
                                    CallerRelativePath = callerRelativePath,
                                    CallerLine = CodeReviewSyntaxTree.StartLine(invocation)
                                }));
                            }
                            break;
                    }
                }

                return (file, deeClassName, orchestrationHits);
            })
            .ToList();

        var deeClassInfo = new DeeClassInfo();
        var orchestrationMap = new Dictionary<string, OrchestrationCallSite>(StringComparer.Ordinal);

        foreach (var (file, deeClassName, orchestrationHits) in perFile)
        {
            if (deeClassName is not null)
            {
                deeClassInfo.Names.Add(deeClassName);
                deeClassInfo.FileByName[deeClassName] = file;
            }

            foreach (var (method, site) in orchestrationHits)
            {
                orchestrationMap.TryAdd(method, site);
            }
        }

        return (deeClassInfo, orchestrationMap);
    }

    /// <summary>
    /// Combines <see cref="GetConstantStringMap"/> and <see cref="GetDeeInvocationMap"/> into a single
    /// per-file syntax-tree walk over the Both scope -- callers needing both (as
    /// <c>CmFrameworkScanner</c> always does, since the invocation map needs the constants map to
    /// resolve expressions anyway) should call this directly instead of the two single-purpose methods
    /// back to back. Constants must still be fully collected (a global constant can be referenced from a
    /// different file than the one that declares it) before any candidate invocation is resolved, so this
    /// stays a two-step process -- but both steps read from the SAME single per-file walk's already-collected
    /// candidates, rather than re-walking the tree a second time to find invocations.
    /// </summary>
    public static (ConstantStringMap Constants, Dictionary<string, List<DeeInvocationRecord>> InvocationMap) GetConstantsAndDeeInvocationMap(
        string repositoryRoot, HashSet<string> deeClassNames, CodeReviewFileCache? fileCache = null)
    {
        fileCache ??= new CodeReviewFileCache();
        var files = CodeReviewPaths.GetFiles(repositoryRoot, CodeReviewPaths.ResolveRoots(repositoryRoot, ReviewScope.Both));

        // Parallel per-file scan, AsOrdered so the sequential merges below (both the "first file wins" for
        // a duplicate global constant name, and the per-DEE invocation record list order) exactly match
        // what GetConstantStringMap/GetDeeInvocationMap would have produced running sequentially/on their own.
        var perFile = files
            .AsParallel().AsOrdered()
            .Select(file =>
            {
                var source = fileCache.Get(file);
                var callerRelativePath = CodeReviewPaths.GetRelativePath(repositoryRoot, file);
                var localConstants = new Dictionary<string, string>(StringComparer.Ordinal);
                var candidates = new List<(InvocationExpressionSyntax Invocation, string Arg)>();

                foreach (var node in source.Root.DescendantNodes())
                {
                    switch (node)
                    {
                        case VariableDeclaratorSyntax declarator when IsConstStringDeclarator(declarator, out var value):
                            localConstants[declarator.Identifier.Text] = value;
                            break;
                        case InvocationExpressionSyntax invocation:
                            var arg = GetDeeActionArgument(invocation);
                            if (arg is not null)
                            {
                                candidates.Add((invocation, arg));
                            }
                            break;
                    }
                }

                return (file, callerRelativePath, source.MethodRanges, localConstants, candidates);
            })
            .ToList();

        var constants = new ConstantStringMap();
        foreach (var (file, _, _, localConstants, _) in perFile)
        {
            constants.PerFile[file] = localConstants;
            foreach (var (ident, value) in localConstants)
            {
                if (!constants.Global.ContainsKey(ident))
                {
                    constants.Global[ident] = value;
                }
            }
        }

        var map = new Dictionary<string, List<DeeInvocationRecord>>(StringComparer.Ordinal);
        foreach (var (_, callerRelativePath, methodRanges, localConstants, candidates) in perFile)
        {
            foreach (var (invocation, arg) in candidates)
            {
                var resolved = ResolveDeeActionExpression(arg, localConstants, constants.Global);
                if (resolved is null || !deeClassNames.Contains(resolved))
                {
                    continue;
                }

                var lineNumber = CodeReviewSyntaxTree.StartLine(invocation);
                if (!map.TryGetValue(resolved, out var list))
                {
                    list = new List<DeeInvocationRecord>();
                    map[resolved] = list;
                }

                list.Add(new DeeInvocationRecord
                {
                    DeeName = resolved,
                    CallerRelativePath = callerRelativePath,
                    CallerLine = lineNumber,
                    CallerMethodName = CmMethodAnalyzer.GetMethodNameForLine(methodRanges, lineNumber)
                });
            }
        }

        return (constants, map);
    }

    private static bool InheritsFrom(ClassDeclarationSyntax classDeclaration, string baseTypeName) =>
        classDeclaration.BaseList?.Types.Any(baseType => CodeReviewSyntaxTree.LastIdentifierSegment(baseType.Type.ToString()) == baseTypeName) ?? false;

    /// <summary>Collects <c>const string</c> declarations (field- or local-level) per file and globally, for resolving DEE action expressions that reference a constant by name.</summary>
    public static ConstantStringMap GetConstantStringMap(IReadOnlyList<string> files, CodeReviewFileCache? fileCache = null)
    {
        fileCache ??= new CodeReviewFileCache();
        var map = new ConstantStringMap();

        foreach (var file in files)
        {
            var root = fileCache.Get(file).Root;
            var localMap = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var declarator in root.DescendantNodes().OfType<VariableDeclaratorSyntax>())
            {
                if (!IsConstStringDeclarator(declarator, out var value))
                {
                    continue;
                }

                var ident = declarator.Identifier.Text;
                localMap[ident] = value;
                if (!map.Global.ContainsKey(ident))
                {
                    map.Global[ident] = value;
                }
            }

            map.PerFile[file] = localMap;
        }

        return map;
    }

    private static bool IsConstStringDeclarator(VariableDeclaratorSyntax declarator, out string value)
    {
        value = string.Empty;

        if (declarator.Parent is not VariableDeclarationSyntax declaration || declaration.Type.ToString() != "string")
        {
            return false;
        }

        var modifiers = declaration.Parent switch
        {
            FieldDeclarationSyntax field => field.Modifiers,
            LocalDeclarationStatementSyntax local => local.Modifiers,
            _ => default
        };

        if (!modifiers.Any(m => m.IsKind(SyntaxKind.ConstKeyword)))
        {
            return false;
        }

        if (declarator.Initializer?.Value is not LiteralExpressionSyntax literal || !literal.IsKind(SyntaxKind.StringLiteralExpression))
        {
            return false;
        }

        value = literal.Token.ValueText;
        return true;
    }

    /// <summary>Resolves a DEE action-call argument expression to a literal string, via a string literal or a local/global constant lookup. Returns null if it can't be resolved (e.g. a runtime-computed expression).</summary>
    public static string? ResolveDeeActionExpression(string expression, IReadOnlyDictionary<string, string> localConstants, IReadOnlyDictionary<string, string> globalConstants)
    {
        var trimmed = expression.Trim();

        var literalMatch = LiteralStringRegex.Match(trimmed);
        if (literalMatch.Success)
        {
            return literalMatch.Groups["val"].Value;
        }

        var identifier = trimmed;
        var dotIndex = trimmed.LastIndexOf('.');
        if (dotIndex >= 0)
        {
            identifier = trimmed[(dotIndex + 1)..];
        }

        if (!IdentifierRegex.IsMatch(identifier))
        {
            return null;
        }

        if (localConstants.TryGetValue(identifier, out var localValue))
        {
            return localValue;
        }

        if (globalConstants.TryGetValue(identifier, out var globalValue))
        {
            return globalValue;
        }

        return null;
    }

    /// <summary>Maps each known DEE class name to every resolvable call site (ExecuteDeeAction/.Load) that invokes it, across DEE and Orchestration code.</summary>
    public static Dictionary<string, List<DeeInvocationRecord>> GetDeeInvocationMap(string repositoryRoot, HashSet<string> deeClassNames, CodeReviewFileCache? fileCache = null)
    {
        fileCache ??= new CodeReviewFileCache();
        var files = CodeReviewPaths.GetFiles(repositoryRoot, CodeReviewPaths.ResolveRoots(repositoryRoot, ReviewScope.Both));
        var constants = GetConstantStringMap(files, fileCache);
        var map = new Dictionary<string, List<DeeInvocationRecord>>(StringComparer.Ordinal);

        // Scan files in parallel (independent per file), but AsOrdered so the per-file record lists
        // come back in file order -- the merge below preserves the exact order the sequential loop
        // produced, which matters because the cross-reference uses the first caller of each DEE.
        var perFile = files
            .AsParallel().AsOrdered()
            .Select(file =>
            {
                var source = fileCache.Get(file);
                var methodRanges = source.MethodRanges;
                var localConstants = constants.PerFile.TryGetValue(file, out var lc) ? lc : new Dictionary<string, string>();
                var callerRelativePath = CodeReviewPaths.GetRelativePath(repositoryRoot, file);
                var records = new List<DeeInvocationRecord>();

                foreach (var invocation in source.Root.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    var arg = GetDeeActionArgument(invocation);
                    if (arg is null)
                    {
                        continue;
                    }

                    var resolved = ResolveDeeActionExpression(arg, localConstants, constants.Global);
                    if (resolved is null || !deeClassNames.Contains(resolved))
                    {
                        continue;
                    }

                    var lineNumber = CodeReviewSyntaxTree.StartLine(invocation);
                    records.Add(new DeeInvocationRecord
                    {
                        DeeName = resolved,
                        CallerRelativePath = callerRelativePath,
                        CallerLine = lineNumber,
                        CallerMethodName = CmMethodAnalyzer.GetMethodNameForLine(methodRanges, lineNumber)
                    });
                }

                return records;
            })
            .ToList();

        foreach (var record in perFile.SelectMany(records => records))
        {
            if (!map.TryGetValue(record.DeeName, out var list))
            {
                list = new List<DeeInvocationRecord>();
                map[record.DeeName] = list;
            }
            list.Add(record);
        }

        return map;
    }

    /// <summary>Maps each Orchestration method name to the first DEE file/line that calls it, for the reverse (DEE-called-Orchestration) cross-reference.</summary>
    public static Dictionary<string, OrchestrationCallSite> GetOrchestrationCallSitesInDees(string repositoryRoot, CodeReviewFileCache? fileCache = null)
    {
        fileCache ??= new CodeReviewFileCache();
        var map = new Dictionary<string, OrchestrationCallSite>(StringComparer.Ordinal);
        var files = CodeReviewPaths.GetFiles(repositoryRoot, CodeReviewPaths.ResolveRoots(repositoryRoot, ReviewScope.DEEs));

        // Parallel per-file scan, AsOrdered + first-wins merge below, so the recorded call site for
        // each method is the same one the sequential loop kept (first in file, then node, order).
        var perFile = files
            .AsParallel().AsOrdered()
            .Select(file =>
            {
                var callerRelativePath = CodeReviewPaths.GetRelativePath(repositoryRoot, file);
                var hits = new List<(string Method, OrchestrationCallSite Site)>();

                foreach (var invocation in fileCache.Get(file).Root.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    var method = GetOrchestrationCallMethod(invocation);
                    if (method is not null)
                    {
                        hits.Add((method, new OrchestrationCallSite
                        {
                            CallerRelativePath = callerRelativePath,
                            CallerLine = CodeReviewSyntaxTree.StartLine(invocation)
                        }));
                    }
                }

                return hits;
            })
            .ToList();

        foreach (var (method, site) in perFile.SelectMany(hits => hits))
        {
            map.TryAdd(method, site);
        }

        return map;
    }
}
