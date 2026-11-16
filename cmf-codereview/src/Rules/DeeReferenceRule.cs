using Cmf.Cli.Plugin.CodeReview.Enums;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cmf.Cli.Plugin.CodeReview.Rules;

/// <summary>Checks reference declarations and type/extension imports used in deployed DEE methods.</summary>
public sealed class DeeReferenceRule(DeeReferenceRepository repository) : IReviewRule
{
    public IEnumerable<string> EscalatableRuleNames => [];

    public IEnumerable<RuleMatch> FindMatches(RuleFileContext context)
    {
        foreach (var type in context.Root.DescendantNodes().OfType<ClassDeclarationSyntax>().Where(c =>
            c.BaseList?.Types.Any(t => CodeReviewSyntaxTree.LastIdentifierSegment(t.Type.ToString()) == "DeeDevBase") == true))
        {
            var methods = type.Members.OfType<MethodDeclarationSyntax>().Where(m =>
                m.Identifier.ValueText is "DeeActionCode" or "DeeTestCondition" && m.Modifiers.Any(SyntaxKind.OverrideKeyword)).ToList();
            var calls = methods.SelectMany(m => m.DescendantNodes().OfType<InvocationExpressionSyntax>())
                .Where(c => c.Expression is IdentifierNameSyntax { Identifier.ValueText: "UseReference" } ||
                    c.Expression is MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax, Name.Identifier.ValueText: "UseReference" }).ToList();
            var project = repository.ForFile(context.FilePath);
            var imports = project is { DefaultsAvailable: true } ? new HashSet<string>(project.DefaultNamespaces, StringComparer.Ordinal) : new(StringComparer.Ordinal);
            var assemblies = project is { DefaultsAvailable: true } ? new HashSet<string>(project.DefaultAssemblies, StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase);
            foreach (var call in calls)
                if (call.ArgumentList.Arguments.FirstOrDefault()?.Expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression) && literal.Token.ValueText.Length > 0)
                    assemblies.Add(AssemblyName(literal.Token.ValueText));
            var unresolvedAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool dynamicReference = false;
            foreach (var call in calls)
            {
                var args = call.ArgumentList.Arguments;
                if (args.Count != 2 || args.Any(a => a.Expression is not LiteralExpressionSyntax literal || !literal.IsKind(SyntaxKind.StringLiteralExpression)))
                {
                    dynamicReference = true;
                    yield return Match("DeeReferenceUnresolved", call, Severity.Medium,
                        "UseReference arguments are not two literal strings; the deployed reference cannot be verified statically.",
                        "Use a supported literal assembly/namespace declaration, or review how the DEE compiler extracts this reference.");
                    continue;
                }
                var assembly = ((LiteralExpressionSyntax)args[0].Expression).Token.ValueText;
                var ns = ((LiteralExpressionSyntax)args[1].Expression).Token.ValueText;
                // Both arguments are independent compiler inputs. An empty call is a no-op.
                if (assembly.Length == 0 && ns.Length == 0) continue;
                var name = AssemblyName(assembly);
                if (ns.Length > 0) imports.Add(ns);
                if (name.Length > 0) assemblies.Add(name);
                if (project is null || !project.MetadataAvailable) continue;
                if (name.Length > 0)
                {
                    if (!project.AssemblyNamespaces.ContainsKey(name) && unresolvedAssemblies.Add(name))
                        yield return Match("DeeReferenceUnresolved", call, Severity.Medium,
                            $"Referenced assembly '{assembly}' is not resolvable from this DEE project's restored dependencies or HintPaths.",
                            "Restore the project and check the assembly name, deployment path, and reference metadata.");

                }
                if (project.DefaultsAvailable && ns.Length > 0 &&
                    !assemblies.Any(a => project.AssemblyNamespaces.TryGetValue(a, out var known) && known.Contains(ns)))
                {
                    var owners = project.AssemblyNamespaces.Where(a => a.Value.Contains(ns)).Select(a => a.Key).ToList();
                    var provenMissing = owners.Count > 0 && !project.SourceAssemblyNames.Overlaps(assemblies) && assemblies.All(project.AssemblyNamespaces.ContainsKey);
                    yield return Match(provenMissing ? "DeeInvalidReference" : "DeeReferenceUnresolved", call,
                        provenMissing ? Severity.High : Severity.Medium,
                        $"Namespace '{ns}' is not verified in any declared or default assembly.",
                        owners.Count > 0 ? $"Declare its assembly explicitly, for example UseReference(\"{owners[0]}.dll\", \"{ns}\")." : "Check the namespace and restore the assembly that supplies it.");
                }
            }
            if (project is null || !project.MetadataAvailable || !project.DefaultsAvailable)
            {
                yield return Match("DeeReferenceUnresolved", type.Identifier.Parent!, Severity.Medium,
                    "DEE reference completeness could not be checked: the owning project, restored dependencies, or compiler default imports are unavailable.",
                    "Restore the DEE project before scanning and ensure the referenced Cmf.Foundation.Common compiler metadata is available.");
                continue;
            }
            if (!project.Complete)
                yield return Match("DeeReferenceUnresolved", type, Severity.Medium,
                    "Some project references could not be resolved; missing-reference validation is limited to symbols that can be bound.",
                    "Restore/build referenced projects and check HintPaths before treating the reference check as complete.");
            if (dynamicReference) continue; // A dynamic declaration might provide the apparently missing import.
            var model = project.Compilation.GetSemanticModel(context.Root.SyntaxTree);
            if (methods.SelectMany(m => model.GetDiagnostics(m.Span)).Any(d => d.Severity == DiagnosticSeverity.Error && d.Id is "CS0246" or "CS0234" or "CS0433" or "CS0104" or "CS0121"))
                yield return Match("DeeReferenceUnresolved", type, Severity.Medium,
                    "Some types/namespaces in deployed DEE code could not be bound against the project metadata; their required references cannot be verified.",
                    "Restore/build the owning project and resolve missing types or namespaces before treating reference validation as complete.");
            var reported = new HashSet<(string, string)>();
            foreach (var node in methods.SelectMany(m => m.DescendantNodes().OfType<SimpleNameSyntax>()))
            {
                var symbol = model.GetSymbolInfo(node).Symbol;
                var usedType = symbol as INamedTypeSymbol;
                var extension = symbol as IMethodSymbol;
                if (usedType?.TypeKind == TypeKind.Error || usedType?.IsAnonymousType == true || usedType?.ContainingNamespace?.IsGlobalNamespace == true) continue;
                var ns = usedType?.ContainingNamespace?.ToDisplayString() ??
                    (extension?.ReducedFrom is not null ? extension.ContainingNamespace.ToDisplayString() : null);
                var assembly = usedType?.ContainingAssembly?.Name ??
                    (extension?.ReducedFrom is not null ? extension.ContainingAssembly.Name : null);
                var containingType = usedType ?? extension?.ContainingType;
                var declarationTree = containingType?.DeclaringSyntaxReferences.FirstOrDefault()?.SyntaxTree;
                if (declarationTree is not null && project.SourceAssemblies.TryGetValue(declarationTree, out var sourceAssembly)) assembly = sourceAssembly;
                if (ns is null || assembly is null || ns.Length == 0) continue;
                if (!project.AssemblyNamespaces.ContainsKey(assembly)) continue; // Source-only project types are not a proven deployable reference.
                var qualified = node.Parent is QualifiedNameSyntax || node.Parent is AliasQualifiedNameSyntax ||
                    node.Parent is MemberAccessExpressionSyntax access && access.Name == node && model.GetSymbolInfo(access.Expression).Symbol is INamespaceSymbol;
                var missingNamespace = !qualified && !imports.Contains(ns);
                var missingAssembly = !assemblies.Contains(assembly);
                if (missingAssembly && containingType is not null)
                {
                    var metadataName = MetadataName(containingType);
                    missingAssembly = !project.Compilation.SourceModule.ReferencedAssemblySymbols.Where(a => assemblies.Contains(a.Name))
                        .Any(a => ProvidesType(a, metadataName, containingType));
                }
                if ((!missingNamespace && !missingAssembly) || !reported.Add((ns, assembly))) continue;
                if (node is IdentifierNameSyntax identifier && model.GetAliasInfo(identifier) is not null) continue;
                yield return Match("DeeMissingReference", node, Severity.High,
                    $"Deployed DEE code uses '{ns}' from '{assembly}', but its required {(missingNamespace ? "namespace import" : "assembly reference")} is not supplied by UseReference or compiler defaults.",
                    $"Add UseReference(\"{(missingAssembly ? assembly + ".dll" : "")}\", \"{(missingNamespace ? ns : "")}\") inside DeeActionCode.");
            }
        }
    }

    private static bool ProvidesType(IAssemblySymbol assembly, string metadataName, INamedTypeSymbol expected)
    {
        var supplied = assembly.GetTypeByMetadataName(metadataName) ?? assembly.ResolveForwardedType(metadataName);
        return SymbolEqualityComparer.Default.Equals(supplied, expected.OriginalDefinition);
    }
    private static string MetadataName(INamedTypeSymbol type) =>
        type.ContainingType is not null ? MetadataName(type.ContainingType) + "+" + type.MetadataName :
        (type.ContainingNamespace.IsGlobalNamespace ? "" : type.ContainingNamespace.ToDisplayString() + ".") + type.MetadataName;

    private static string AssemblyName(string value)
    {
        var normalized = value.Replace('\\', '/');
        var prefix = normalized.LastIndexOf('%');
        if (prefix >= 0) normalized = normalized[(prefix + 1)..];
        return Path.GetFileNameWithoutExtension(normalized);
    }
    private static RuleMatch Match(string rule, SyntaxNode node, Severity severity, string description, string fix) =>
        new(rule, node.GetLocation().GetLineSpan().StartLinePosition.Line + 1, severity, description, fix);
}