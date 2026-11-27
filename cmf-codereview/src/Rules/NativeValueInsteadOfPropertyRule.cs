using Cmf.Cli.Plugin.CodeReview.Enums;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cmf.Cli.Plugin.CodeReview.Rules;

/// <summary>Suggests typed access only for a verified scalar wrapper on a resolved platform type.</summary>
public sealed class NativeValueInsteadOfPropertyRule : IReviewRule
{
    public IEnumerable<string> EscalatableRuleNames => [];

    public IEnumerable<RuleMatch> FindMatches(RuleFileContext context)
    {
        var project = context.ReferenceRepository?.ForFile(context.FilePath);
        if (project is null || !project.Compilation.ContainsSyntaxTree(context.Root.SyntaxTree)) yield break;
        var model = project.Compilation.GetSemanticModel(context.Root.SyntaxTree);
        foreach (var call in context.Root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (call.Expression is not MemberAccessExpressionSyntax { Name: GenericNameSyntax name } member ||
                name.Identifier.ValueText != "GetNativeValue" || call.ArgumentList.Arguments.Count != 1 ||
                model.GetConstantValue(call.ArgumentList.Arguments[0].Expression) is not { HasValue: true, Value: "Name" } ||
                model.GetSymbolInfo(call).Symbol is not IMethodSymbol { TypeArguments.Length: 1 } method ||
                method.TypeArguments[0].SpecialType != SpecialType.System_String ||
                model.GetTypeInfo(member.Expression).Type is not INamedTypeSymbol type ||
                !EntityCatalog.NativeNameWrapperTypes.Contains(type.ToDisplayString())) continue;
            var property = FindName(type);
            if (property?.Type.SpecialType != SpecialType.System_String || !VerifiedSourceGetter(project.Compilation, property, method)) continue;
            yield return new RuleMatch("NativeValueInsteadOfProperty", CodeReviewSyntaxTree.StartLine(call), Severity.Medium,
                "Name has an equivalent scalar typed getter on this resolved entity type.",
                $"Use {member.Expression}.Name instead of GetNativeValue<string>(\"Name\").");
        }
    }

    private static IPropertySymbol? FindName(INamedTypeSymbol type)
    {
        for (var current = type; current is not null; current = current.BaseType)
            if (current.GetMembers("Name").OfType<IPropertySymbol>().FirstOrDefault() is { } property) return property;
        return type.AllInterfaces.SelectMany(t => t.GetMembers("Name")).OfType<IPropertySymbol>().FirstOrDefault();
    }

    private static bool VerifiedSourceGetter(Compilation compilation, IPropertySymbol property, IMethodSymbol usedMethod)
    {
        var syntax = property.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() as PropertyDeclarationSyntax;
        if (syntax is null) return true; // The exact platform type/property is in the verified catalog.
        if (property.ContainingType.TypeKind == TypeKind.Interface) return true;
        var getter = syntax.AccessorList?.Accessors.FirstOrDefault(a => a.Keyword.ValueText == "get");
        var expression = syntax.ExpressionBody?.Expression ?? getter?.ExpressionBody?.Expression ??
            (getter?.Body?.Statements is { Count: 1 } statements && statements[0] is ReturnStatementSyntax ret ? ret.Expression : null);
        if (expression is not InvocationExpressionSyntax call || call.ArgumentList.Arguments.Count != 1 ||
            call.Expression is not GenericNameSyntax && call.Expression is not MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax }) return false;
        var model = compilation.GetSemanticModel(call.SyntaxTree);
        return model.GetSymbolInfo(call).Symbol is IMethodSymbol method &&
            SymbolEqualityComparer.Default.Equals(method.OriginalDefinition, usedMethod.OriginalDefinition) &&
            model.GetConstantValue(call.ArgumentList.Arguments[0].Expression) is { HasValue: true, Value: "Name" };
    }
}
