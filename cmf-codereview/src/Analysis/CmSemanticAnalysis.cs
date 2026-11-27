using Cmf.Cli.Plugin.CodeReview.Rules;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cmf.Cli.Plugin.CodeReview;

/// <summary>Conservative type evidence from the scan's shared project compilation.</summary>
public static class CmSemanticAnalysis
{
    public static INamedTypeSymbol? ResolveType(RuleFileContext context, ExpressionSyntax expression)
    {
        var project = context.ReferenceRepository?.ForFile(context.FilePath);
        if (project is null || !project.Compilation.ContainsSyntaxTree(expression.SyntaxTree)) return null;
        return project.Compilation.GetSemanticModel(expression.SyntaxTree).GetTypeInfo(expression).Type as INamedTypeSymbol;
    }

    public static bool IsResolvedNonEntity(RuleFileContext context, ExpressionSyntax access)
    {
        var receiver = CodeReviewSyntaxTree.GetMemberTarget(access);
        var type = receiver is null ? null : ResolveType(context, receiver);
        if (type is null || type.TypeKind is TypeKind.Error or TypeKind.TypeParameter) return false;
        var hierarchy = new List<INamedTypeSymbol>();
        for (var current = type; current is not null; current = current.BaseType) hierarchy.Add(current);
        hierarchy.AddRange(type.AllInterfaces);
        if (hierarchy.Any(t => t.TypeKind == TypeKind.Error || t.Name is "EntityBase" or "IEntityBase")) return false;
        return !hierarchy.SelectMany(t => t.GetMembers()).Any(member =>
            member.Name is "LoadReference" or "LoadReferences" or "LoadRelations");
    }
}
