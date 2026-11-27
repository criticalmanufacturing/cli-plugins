using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cmf.Cli.Plugin.CodeReview;

/// <summary>Proves a bounded, same-block preload followed by foreach over the same collection.</summary>
public static class CmPreloadAnalyzer
{
    private static readonly HashSet<string> PreservingMethods = new(StringComparer.Ordinal)
        { "Where", "OrderBy", "OrderByDescending", "ThenBy", "ThenByDescending", "Skip", "Take", "Distinct", "ToList", "ToArray" };
    private static readonly HashSet<string> PreloadMethods = new(StringComparer.Ordinal)
        { "LoadReference", "LoadReferences", "LoadRelations" };

    public static bool IsPreloadedBeforeLoop(ExpressionSyntax access)
    {
        if (CodeReviewSyntaxTree.GetMemberTarget(access) is not MemberAccessExpressionSyntax reference ||
            reference.Expression is not IdentifierNameSyntax item) return false;
        var loop = access.Ancestors().OfType<ForEachStatementSyntax>().FirstOrDefault();
        if (loop is null || item.Identifier.ValueText != loop.Identifier.ValueText ||
            Scope(access) != Scope(loop) || loop.Parent is not BlockSyntax block) return false;
        var collection = CollectionRoot(loop.Expression);
        if (collection is null) return false;
        if (block.DescendantNodes().OfType<VariableDeclaratorSyntax>().Any(v => v.SpanStart < access.SpanStart &&
            v.Initializer is { } initializer && Mentions(initializer, collection))) return false;
        foreach (var statement in block.Statements.TakeWhile(s => s != loop).Reverse())
        {
            if (statement is not ExpressionStatementSyntax { Expression: InvocationExpressionSyntax call } ||
                call.Expression is not MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax receiver } member ||
                receiver.Identifier.ValueText != collection || !PreloadMethods.Contains(member.Name.Identifier.ValueText)) continue;
            if (member.Name.Identifier.ValueText != "LoadRelations" &&
                !ReferenceNames(call).Contains(reference.Name.Identifier.ValueText, StringComparer.Ordinal)) continue;
            var between = block.DescendantNodes().Where(n => n.SpanStart >= statement.Span.End && n.Span.End <= access.SpanStart);
            if (!between.Any(n => Invalidates(n, collection, loop.Identifier.ValueText))) return true;
        }
        return false;
    }

    private static SyntaxNode? Scope(SyntaxNode node) => node.Ancestors().FirstOrDefault(n =>
        n is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax or AccessorDeclarationSyntax);

    private static string? CollectionRoot(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax id => id.Identifier.ValueText,
        ParenthesizedExpressionSyntax paren => CollectionRoot(paren.Expression),
        InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax member } call
            when PreservingMethods.Contains(member.Name.Identifier.ValueText) &&
                !call.ArgumentList.DescendantNodes().Any(n => n is AssignmentExpressionSyntax or PostfixUnaryExpressionSyntax ||
                    n is PrefixUnaryExpressionSyntax prefix && (prefix.IsKind(SyntaxKind.PreIncrementExpression) ||
                        prefix.IsKind(SyntaxKind.PreDecrementExpression))) => CollectionRoot(member.Expression),
        _ => null
    };

    private static IEnumerable<string> ReferenceNames(InvocationExpressionSyntax call)
    {
        var argument = call.ArgumentList.Arguments.FirstOrDefault()?.Expression;
        if (argument is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
            return [literal.Token.ValueText];
        var initializer = argument switch
        {
            ArrayCreationExpressionSyntax array => array.Initializer,
            ImplicitArrayCreationExpressionSyntax array => array.Initializer,
            _ => null
        };
        return initializer is not null && initializer.Expressions.All(e => e is LiteralExpressionSyntax l && l.IsKind(SyntaxKind.StringLiteralExpression))
            ? initializer.Expressions.Cast<LiteralExpressionSyntax>().Select(l => l.Token.ValueText) : [];
    }

    private static bool Mentions(SyntaxNode node, string name) => node.DescendantNodesAndSelf()
        .OfType<IdentifierNameSyntax>().Any(id => id.Identifier.ValueText == name);

    private static bool Invalidates(SyntaxNode node, string collection, string item) => node switch
    {
        AssignmentExpressionSyntax assignment => Mentions(assignment.Left, collection) || Mentions(assignment.Left, item),
        PrefixUnaryExpressionSyntax prefix when prefix.IsKind(SyntaxKind.PreIncrementExpression) || prefix.IsKind(SyntaxKind.PreDecrementExpression)
            => Mentions(prefix, collection) || Mentions(prefix, item),
        PostfixUnaryExpressionSyntax postfix => Mentions(postfix, collection) || Mentions(postfix, item),
        VariableDeclaratorSyntax { Initializer: { } value } => Mentions(value, collection) || Mentions(value, item),
        ArgumentSyntax argument => Mentions(argument, collection) || Mentions(argument, item),
        InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax member } =>
            Mentions(member.Expression, item) || Mentions(member.Expression, collection) &&
                !PreloadMethods.Contains(member.Name.Identifier.ValueText) && !PreservingMethods.Contains(member.Name.Identifier.ValueText),
        _ => false
    };
}
