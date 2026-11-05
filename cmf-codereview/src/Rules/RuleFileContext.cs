using System.Collections.Concurrent;
using Cmf.Cli.Plugin.CodeReview.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cmf.Cli.Plugin.CodeReview.Rules;

/// <summary>Everything a rule needs to decide matches for one file, precomputed once per file by the orchestrator.</summary>
public class RuleFileContext
{
    public required CompilationUnitSyntax Root { get; init; }
    public required IReadOnlyList<string> Lines { get; init; }
    public required LoopState LoopState { get; init; }
    public required List<MethodRange> MethodRanges { get; init; }
    public required bool IsExtensionFile { get; init; }
    public required bool IsOrchestrationFile { get; init; }
    public required bool IsDeeFile { get; init; }
    public required bool IsOneTimeMigrationFile { get; init; }
    public ProcessRuleInfo? ProcessRule { get; init; }
    public ProcessRuleRepositoryContext? ProcessRuleRepository { get; init; }
    public IReadOnlyList<string> ProcessRuleOrderProblems { get; init; } = Array.Empty<string>();
    public required bool ContainsExtensionMethod { get; init; }

    /// <summary>The repository's reflection-resolved entity catalog, shared across every file's context in a scan.</summary>
    public required EntityCatalog EntityCatalog { get; init; }
    public required HashSet<string> ConfigVarNames { get; init; }
    public required HashSet<string> ConfigWriteVarNames { get; init; }
    public required MethodCallGraph CallGraph { get; init; }
    public required ConcurrentDictionary<string, LoopExposure?> LoopExposureCache { get; init; }

    public string? GetMethodNameForLine(int line) => CmMethodAnalyzer.GetMethodNameForLine(MethodRanges, line);

    // GetOrAdd may compute the same exposure twice under a race, but FindLoopExposure is pure, so
    // duplicate work is the only cost -- never an inconsistent result.
    public LoopExposure? GetLoopExposure(string methodName) =>
        LoopExposureCache.GetOrAdd(methodName, m => CmLoopExposureFinder.FindLoopExposure(CallGraph, m));

    /// <summary>Note to append to a finding's description when its enclosing method is itself called from a loop elsewhere -- empty string when it isn't.</summary>
    public string GetTransitiveLoopNote(string? methodName)
    {
        if (methodName is null)
        {
            return string.Empty;
        }

        var exposure = GetLoopExposure(methodName);
        if (exposure is null || exposure.Kind != LoopExposure.CalledFromLoopKind)
        {
            return string.Empty;
        }

        var chain = CmLoopExposureFinder.FormatChain(methodName, exposure);
        return $" On top of that, the enclosing method '{methodName}' is itself called from another loop, compounding the cost further: {chain}.";
    }

    public static RuleFileContext Build(IReadOnlyList<string> lines, string file, MethodCallGraph callGraph, ConcurrentDictionary<string, LoopExposure?> loopExposureCache, EntityCatalog entityCatalog)
    {
        var (_, root) = CodeReviewSyntaxTree.Parse(lines);
        return Build(root, lines, CmLoopAnalyzer.GetLoopState(root), CmMethodAnalyzer.GetMethodRanges(root), file, callGraph, loopExposureCache, entityCatalog);
    }

    /// <summary>Builds from a scan's shared per-file cache, reusing its parse and derived analyses instead of re-doing them.</summary>
    public static RuleFileContext Build(CachedSourceFile source, MethodCallGraph callGraph, ConcurrentDictionary<string, LoopExposure?> loopExposureCache, EntityCatalog entityCatalog,
        ProcessRuleRepositoryContext? processRuleRepository = null) =>
        Build(source.Root, source.Lines, source.LoopState, source.MethodRanges, source.Path, callGraph, loopExposureCache, entityCatalog, processRuleRepository);

    private static RuleFileContext Build(CompilationUnitSyntax root, IReadOnlyList<string> lines, LoopState loopState, List<MethodRange> methodRanges,
        string file, MethodCallGraph callGraph, ConcurrentDictionary<string, LoopExposure?> loopExposureCache, EntityCatalog entityCatalog,
        ProcessRuleRepositoryContext? processRuleRepository = null)
    {
        var isExtensionFile = CodeReviewPaths.IsExtensionFile(file);
        var isOrchestrationFile = CodeReviewPaths.IsOrchestrationFile(file);
        var isDeeFile = CodeReviewPaths.IsDeeFile(file);
        var isOneTimeMigrationFile = CodeReviewPaths.IsOneTimeMigrationFile(file);

        var containsExtensionMethod = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Any(m => m.ParameterList.Parameters.FirstOrDefault()?.Modifiers.Any(mod => mod.IsKind(SyntaxKind.ThisKeyword)) == true);

        var configVarNames = FindConfigVarNames(root);
        var configWriteVarNames = isOneTimeMigrationFile
            ? new HashSet<string>(StringComparer.Ordinal)
            : FindConfigWriteVarNames(root, configVarNames);

        return new RuleFileContext
        {
            Root = root,
            Lines = lines,
            LoopState = loopState,
            MethodRanges = methodRanges,
            IsExtensionFile = isExtensionFile,
            IsOrchestrationFile = isOrchestrationFile,
            IsDeeFile = isDeeFile,
            IsOneTimeMigrationFile = isOneTimeMigrationFile,
            ProcessRule = processRuleRepository?.GetInfo(file) ?? ProcessRuleRepositoryContext.ClassifyPath(file),
            ProcessRuleRepository = processRuleRepository,
            ProcessRuleOrderProblems = processRuleRepository?.GetOrderProblems(file) ?? Array.Empty<string>(),
            ContainsExtensionMethod = containsExtensionMethod,
            EntityCatalog = entityCatalog,
            ConfigVarNames = configVarNames,
            ConfigWriteVarNames = configWriteVarNames,
            CallGraph = callGraph,
            LoopExposureCache = loopExposureCache
        };
    }

    private static HashSet<string> FindConfigVarNames(CompilationUnitSyntax root)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var declarator in root.DescendantNodes().OfType<VariableDeclaratorSyntax>())
        {
            if (declarator.Parent is VariableDeclarationSyntax { Type: IdentifierNameSyntax { Identifier.Text: "IConfig" } })
            {
                names.Add(declarator.Identifier.Text);
            }
        }

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax
                {
                    Expression: IdentifierNameSyntax { Identifier.Text: "Config" },
                    Name.Identifier.Text: "TryGetConfig"
                })
            {
                continue;
            }

            foreach (var argument in invocation.ArgumentList.Arguments)
            {
                if (!argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword))
                {
                    continue;
                }

                var name = argument.Expression switch
                {
                    DeclarationExpressionSyntax { Designation: SingleVariableDesignationSyntax designation } => designation.Identifier.Text,
                    IdentifierNameSyntax identifier => identifier.Identifier.Text,
                    _ => null
                };

                if (name is not null)
                {
                    names.Add(name);
                }
            }
        }

        return names;
    }

    private static HashSet<string> FindConfigWriteVarNames(CompilationUnitSyntax root, HashSet<string> configVarNames)
    {
        var writeVarNames = new HashSet<string>(StringComparer.Ordinal);

        var assignedNames = root.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.IsKind(SyntaxKind.SimpleAssignmentExpression))
            .Select(a => a.Left)
            .OfType<MemberAccessExpressionSyntax>()
            .Where(ma => ma.Name.Identifier.Text == "Value" && ma.Expression is IdentifierNameSyntax)
            .Select(ma => ((IdentifierNameSyntax)ma.Expression).Identifier.Text)
            .ToHashSet(StringComparer.Ordinal);

        var savedNames = root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Select(i => i.Expression)
            .OfType<MemberAccessExpressionSyntax>()
            .Where(ma => ma.Name.Identifier.Text == "Save" && ma.Expression is IdentifierNameSyntax)
            .Select(ma => ((IdentifierNameSyntax)ma.Expression).Identifier.Text)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var name in configVarNames)
        {
            if (assignedNames.Contains(name) && savedNames.Contains(name))
            {
                writeVarNames.Add(name);
            }
        }

        return writeVarNames;
    }
}
