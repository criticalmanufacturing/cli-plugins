using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cmf.Cli.Plugin.CodeReview;

/// <summary>
/// Decides whether a flagged entity-reference access (<c>x.Material.Description</c>) actually
/// repeats its lazy-load cost across loop/lambda iterations. CM Framework lazy loading caches on
/// the entity instance after the first dereference, so the cost is per DISTINCT receiver instance:
/// <c>item.Material.X</c> with <c>item</c> as the iteration variable loads once per item, but
/// <c>input.Material.X</c> with a loop-invariant receiver loads exactly once no matter how many
/// times the loop runs. Only the varying case is a real loop-multiplied cost.
/// </summary>
public static class CmReceiverVariance
{
    /// <summary>
    /// True when the access's receiver can refer to a different instance on different iterations of
    /// any enclosing loop or lambda: the receiver root is the iteration variable, a lambda
    /// parameter, something declared/assigned inside the loop, or something we can't resolve to a
    /// simple identifier at all (indexers, invocation results — conservatively treated as varying).
    /// </summary>
    public static bool ReceiverVariesPerIteration(ExpressionSyntax access)
    {
        // The lazy-loaded reference is the access's target (x.Material); what varies or not across
        // iterations is that reference's own receiver root (x).
        var entityReference = CodeReviewSyntaxTree.GetMemberTarget(access);
        var root = GetRootExpression(entityReference);

        if (root is ThisExpressionSyntax or BaseExpressionSyntax)
        {
            return false;
        }

        if (root is not IdentifierNameSyntax identifier)
        {
            return true;
        }

        var rootName = identifier.Identifier.Text;

        foreach (var ancestor in access.Ancestors())
        {
            switch (ancestor)
            {
                case MethodDeclarationSyntax or LocalFunctionStatementSyntax or PropertyDeclarationSyntax:
                    return false;

                case SimpleLambdaExpressionSyntax simpleLambda:
                    if (simpleLambda.Parameter.Identifier.Text == rootName || IsMutatedWithin(simpleLambda.Body, rootName))
                    {
                        return true;
                    }
                    break;

                case ParenthesizedLambdaExpressionSyntax lambda:
                    if (lambda.ParameterList.Parameters.Any(p => p.Identifier.Text == rootName) || IsMutatedWithin(lambda.Body, rootName))
                    {
                        return true;
                    }
                    break;

                case ForEachStatementSyntax forEach:
                    if (forEach.Identifier.Text == rootName || IsMutatedWithin(forEach.Statement, rootName))
                    {
                        return true;
                    }
                    break;

                case ForEachVariableStatementSyntax forEachVariable:
                    // Deconstructing foreach (var (a, b) in ...): resolving which names it binds is
                    // uncommon enough that we stay conservative and treat the receiver as varying.
                    _ = forEachVariable;
                    return true;

                case ForStatementSyntax forStatement:
                    if (IsMutatedWithin(forStatement.Statement, rootName))
                    {
                        return true;
                    }
                    break;

                case WhileStatementSyntax whileStatement:
                    if (IsMutatedWithin(whileStatement.Statement, rootName))
                    {
                        return true;
                    }
                    break;

                case DoStatementSyntax doStatement:
                    if (IsMutatedWithin(doStatement.Statement, rootName))
                    {
                        return true;
                    }
                    break;
            }
        }

        return false;
    }

    /// <summary>The innermost expression a member-access chain hangs off: <c>a</c> for <c>a.b.Material</c>, the conditional-access receiver for <c>a?.Material</c>.</summary>
    private static ExpressionSyntax? GetRootExpression(ExpressionSyntax? expression)
    {
        while (expression is not null)
        {
            var next = CodeReviewSyntaxTree.GetMemberTarget(expression);
            if (next is null)
            {
                return expression;
            }

            if (ReferenceEquals(next, expression))
            {
                // Non-advancing step: bail out rather than spin. Callers treat a non-identifier
                // root as "varies" (conservative), which is the safe answer here too.
                return null;
            }

            expression = next;
        }

        return null;
    }

    // One mutated-name set per scope node, computed on first use. Without this, every candidate
    // access re-walked its entire enclosing loop body (quadratic: hundreds of accesses x
    // multi-thousand-node bodies in real customization files), which took the whole scan from
    // ~1 minute to many minutes.
    private static readonly ConditionalWeakTable<SyntaxNode, HashSet<string>> MutatedNamesCache = new();

    /// <summary>True when the named identifier is (re)declared or assigned anywhere within the given node — meaning it can hold a different instance on different iterations.</summary>
    private static bool IsMutatedWithin(SyntaxNode scope, string name) =>
        MutatedNamesCache.GetValue(scope, ComputeMutatedNames).Contains(name);

    private static HashSet<string> ComputeMutatedNames(SyntaxNode scope)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var node in scope.DescendantNodes())
        {
            switch (node)
            {
                case VariableDeclaratorSyntax declarator:
                    names.Add(declarator.Identifier.Text);
                    break;
                case SingleVariableDesignationSyntax designation:
                    names.Add(designation.Identifier.Text);
                    break;
                case AssignmentExpressionSyntax { Left: IdentifierNameSyntax assigned }:
                    names.Add(assigned.Identifier.Text);
                    break;
                case ArgumentSyntax { Expression: IdentifierNameSyntax argument } arg when !arg.RefKindKeyword.IsKind(SyntaxKind.None):
                    names.Add(argument.Identifier.Text);
                    break;
                case ForEachStatementSyntax nestedForEach:
                    names.Add(nestedForEach.Identifier.Text);
                    break;
            }
        }

        return names;
    }
}
