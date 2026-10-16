using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cmf.Cli.Plugin.CodeReview;

/// <summary>Parses C# source into a syntax tree once, shared by every analyzer that needs to walk it.</summary>
public static class CodeReviewSyntaxTree
{
    /// <summary>Parses already-loaded source text (e.g. from an in-memory synthetic snippet or a file already read once).</summary>
    public static (SyntaxTree Tree, CompilationUnitSyntax Root) Parse(string sourceText)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceText);
        var root = (CompilationUnitSyntax)tree.GetRoot();
        return (tree, root);
    }

    /// <summary>Parses source given as separate lines (the shape most analyzers already receive/read).</summary>
    public static (SyntaxTree Tree, CompilationUnitSyntax Root) Parse(IReadOnlyList<string> lines) =>
        Parse(string.Join('\n', lines));

    /// <summary>1-based start line for a syntax node, per its location in the tree that produced it.</summary>
    public static int StartLine(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    /// <summary>1-based end line for a syntax node, per its location in the tree that produced it.</summary>
    public static int EndLine(SyntaxNode node) => node.GetLocation().GetLineSpan().EndLinePosition.Line + 1;

    /// <summary>
    /// The simple name of a member access, whether written as a plain member access (<c>a.Foo</c>) or as the
    /// null-conditional shape Roslyn parses <c>a?.Foo</c>/<c>a?.Foo()</c> into (a <see cref="MemberBindingExpressionSyntax"/>
    /// with the receiver hoisted onto the enclosing <see cref="ConditionalAccessExpressionSyntax"/>). Rules that only
    /// care about the accessed/invoked member's name should match through this helper instead of pattern-matching
    /// <see cref="MemberAccessExpressionSyntax"/> directly, or they silently miss every null-conditional call site.
    /// </summary>
    public static SimpleNameSyntax? GetMemberName(ExpressionSyntax expr) => expr switch
    {
        MemberAccessExpressionSyntax memberAccess => memberAccess.Name,
        MemberBindingExpressionSyntax memberBinding => memberBinding.Name,
        _ => null
    };

    /// <summary>The expression a member access or null-conditional member access is dereferencing off of (<c>a</c> in both <c>a.Foo</c> and <c>a?.Foo</c>).</summary>
    public static ExpressionSyntax? GetMemberTarget(ExpressionSyntax expr) => expr switch
    {
        MemberAccessExpressionSyntax memberAccess => memberAccess.Expression,
        MemberBindingExpressionSyntax memberBinding => GetConditionalAccessReceiver(memberBinding),
        _ => null
    };

    /// <summary>
    /// The receiver of a null-conditional member binding. In a chained conditional access
    /// (<c>a?.b?.c</c>) Roslyn nests the tail as its own <see cref="ConditionalAccessExpressionSyntax"/>
    /// whose Expression is the <c>.b</c> binding itself — so the binding's receiver is NOT simply its
    /// parent conditional access's Expression (that would resolve <c>.b</c> to <c>.b</c>, a
    /// non-advancing cycle for receiver-chain walkers). The real receiver hangs off the conditional
    /// access whose WhenNotNull contains this binding: walk up until we cross a WhenNotNull edge.
    /// </summary>
    private static ExpressionSyntax? GetConditionalAccessReceiver(ExpressionSyntax memberBinding)
    {
        for (SyntaxNode node = memberBinding; node.Parent is ExpressionSyntax parent; node = parent)
        {
            if (parent is ConditionalAccessExpressionSyntax conditional && ReferenceEquals(conditional.WhenNotNull, node))
            {
                return conditional.Expression;
            }
        }

        return null;
    }

    /// <summary>The last dotted segment of a type/namespace-qualified name, e.g. "Cmf.Foundation.BusinessObjects.Material" -> "Material".</summary>
    public static string LastIdentifierSegment(string qualifiedName) =>
        qualifiedName.Contains('.') ? qualifiedName[(qualifiedName.LastIndexOf('.') + 1)..] : qualifiedName;
}
