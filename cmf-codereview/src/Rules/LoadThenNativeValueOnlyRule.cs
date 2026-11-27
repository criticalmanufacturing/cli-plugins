using Cmf.Cli.Plugin.CodeReview.Enums;
using Cmf.Cli.Plugin.CodeReview.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cmf.Cli.Plugin.CodeReview.Rules;

/// <summary>Finds bounded entity loads whose subsequent receiver uses are exclusively native-value reads.</summary>
public sealed class LoadThenNativeValueOnlyRule : IReviewRule
{
    public const string RuleName = "LoadThenNativeValueOnly";
    private const string Fix = "Verify that the native values are already available and remove the explicit Load, or source the required values from an already-loaded collection. Keep the load when it is needed to obtain those values.";
    public IEnumerable<string> EscalatableRuleNames => [RuleName];

    public IEnumerable<RuleMatch> FindMatches(RuleFileContext context)
    {
        var project = context.ReferenceRepository?.ForFile(context.FilePath);
        if (project is null || !project.Compilation.ContainsSyntaxTree(context.Root.SyntaxTree)) yield break;
        var model = project.Compilation.GetSemanticModel(context.Root.SyntaxTree);
        var candidates = context.Root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(call => IsCandidate(call, model)).ToHashSet();
        if (candidates.Count == 0) yield break;
        var loopRule = new LoopSensitiveCallRule(RuleName, (call, _) => candidates.Contains(call),
            "Load followed only by native-value reads", Fix, Fix);
        foreach (var match in loopRule.FindMatches(context)) yield return match with { RuleName = RuleName };
        foreach (var call in candidates)
        {
            var line = CodeReviewSyntaxTree.StartLine(call);
            if (context.LoopState.Lines.Contains(line) && !context.LoopState.SingleExecutionLines.Contains(line)) continue;
            var method = context.GetMethodNameForLine(line);
            if (method is not null && context.GetLoopExposure(method)?.Kind == LoopExposure.CalledFromLoopKind) continue;
            yield return new RuleMatch(RuleName, line, Severity.Medium,
                "This entity's subsequent uses in the enclosing scope are only GetNativeValue reads; review whether its explicit Load is necessary.", Fix);
        }
    }

    private static SyntaxNode? Scope(SyntaxNode node) => node.Ancestors().FirstOrDefault(n =>
        n is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax or AccessorDeclarationSyntax);

    private static bool IsCandidate(InvocationExpressionSyntax load, SemanticModel model)
    {
        if (load.Expression is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Load" } member ||
            load.Parent is not ExpressionStatementSyntax { Parent: BlockSyntax } ||
            member.Expression.DescendantNodesAndSelf().Any(n => n is InvocationExpressionSyntax or ConditionalAccessExpressionSyntax) ||
            model.GetTypeInfo(member.Expression).Type is not INamedTypeSymbol type || !IsEntity(type) || Scope(load) is not { } scope) return false;
        var receiver = member.Expression;
        var root = Root(receiver);
        if (root is null || model.GetSymbolInfo(root).Symbol is not { } rootSymbol ||
            model.GetSymbolInfo(receiver).Symbol is not { } receiverSymbol) return false;
        bool Same(ExpressionSyntax expression) => SyntaxFactory.AreEquivalent(receiver, expression) &&
            SymbolEqualityComparer.Default.Equals(receiverSymbol, model.GetSymbolInfo(expression).Symbol) &&
            Root(expression) is { } otherRoot && SymbolEqualityComparer.Default.Equals(rootSymbol, model.GetSymbolInfo(otherRoot).Symbol);
        bool RootUse(SyntaxNode node) => node.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().Any(id =>
            SymbolEqualityComparer.Default.Equals(rootSymbol, model.GetSymbolInfo(id).Symbol));
        // Key-based lookup populates native data; GetNativeValue itself does not fetch missing values.
        if (scope.DescendantNodes().OfType<AssignmentExpressionSyntax>().Any(a => a.SpanStart < load.SpanStart &&
            a.Left is MemberAccessExpressionSyntax key && key.Name.Identifier.ValueText is "Name" or "Id" or "DefinitionId" && Same(key.Expression))) return false;
        // A direct alias or escape anywhere in this callable defeats the local usage proof.
        if (scope.DescendantNodes().OfType<VariableDeclaratorSyntax>().Any(v => v.Initializer?.Value is { } value && Same(Unwrap(value))) ||
            scope.DescendantNodes().OfType<AssignmentExpressionSyntax>().Any(a => Same(Unwrap(a.Right))) ||
            scope.DescendantNodes().OfType<InitializerExpressionSyntax>().Any(i => i.Expressions.Any(e => Same(Unwrap(e)))) ||
            scope.DescendantNodes().OfType<ExpressionSyntax>().Any(e => Same(e) && Scope(e) != scope) ||
            scope.DescendantNodes().OfType<ArgumentSyntax>().Any(a => Unwrap(a.Expression) is IdentifierNameSyntax id &&
                SymbolEqualityComparer.Default.Equals(rootSymbol, model.GetSymbolInfo(id).Symbol))) return false;
        var later = scope.DescendantNodes().Where(n => n.SpanStart > load.Span.End).ToList();
        if (later.OfType<AssignmentExpressionSyntax>().Any(a => RootUse(a.Left)) ||
            later.OfType<ArgumentSyntax>().Any(a => a.RefKindKeyword.Kind() != SyntaxKind.None && RootUse(a)) ||
            later.OfType<PrefixUnaryExpressionSyntax>().Any(a => RootUse(a) && (a.IsKind(SyntaxKind.PreIncrementExpression) || a.IsKind(SyntaxKind.PreDecrementExpression))) ||
            later.OfType<InvocationExpressionSyntax>().Any(c => c.Expression is MemberAccessExpressionSyntax m && RootUse(m.Expression) &&
                !m.Expression.DescendantNodesAndSelf().OfType<ExpressionSyntax>().Any(Same)) ||
            later.OfType<PostfixUnaryExpressionSyntax>().Any(a => RootUse(a))) return false;
        var reads = 0;
        foreach (var expression in later.OfType<ExpressionSyntax>().Where(Same))
        {
            if (Scope(expression) != scope || expression.Parent is not MemberAccessExpressionSyntax access || access.Expression != expression ||
                access.Parent is not InvocationExpressionSyntax call || call.Expression != access ||
                model.GetSymbolInfo(call).Symbol is not IMethodSymbol { Name: "GetNativeValue", TypeArguments.Length: 1, ReducedFrom: null }) return false;
            reads++;
        }
        return reads > 0;
    }

    private static ExpressionSyntax Unwrap(ExpressionSyntax expression) => expression is ParenthesizedExpressionSyntax p ? Unwrap(p.Expression) : expression;
    private static IdentifierNameSyntax? Root(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax id => id,
        MemberAccessExpressionSyntax member => Root(member.Expression),
        _ => null
    };

    private static bool IsEntity(INamedTypeSymbol type)
    {
        var hierarchy = new List<INamedTypeSymbol>();
        for (var current = type; current is not null; current = current.BaseType) hierarchy.Add(current);
        hierarchy.AddRange(type.AllInterfaces);
        return !hierarchy.Any(t => t.TypeKind == TypeKind.Error) && hierarchy.Any(t => t.ToDisplayString() is
            "Cmf.Foundation.BusinessObjects.EntityBase" or "Cmf.Foundation.BusinessObjects.Abstractions.IEntityBase");
    }
}
