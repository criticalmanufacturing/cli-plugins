using Cmf.Cli.Plugin.CodeReview.Models;
using Cmf.Cli.Plugin.CodeReview.Enums;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cmf.Cli.Plugin.CodeReview.Rules;

/// <summary>
/// A property dereferenced off a known entity-type reference (<c>x.Material.Description</c>, or the
/// null-conditional shape <c>x.Material?.Description</c>) that's worse for some reason specific to
/// the property being accessed. LoadReferenceForFkOnly/ExcessiveLazyLoading are both one instantiation
/// of this class each; a future rule of the same "X.EntityType.Property" shape is another
/// instantiation, not new code.
/// </summary>
public class EntityPropertyAccessRule : IReviewRule
{
    private readonly string _ruleName;
    private readonly Func<string, bool> _propertyNameMatches;
    private readonly Severity _severity;
    private readonly string _description;
    private readonly string _suggestedFix;
    private readonly Func<RuleFileContext, ExpressionSyntax, int, bool>? _extraCondition;

    public EntityPropertyAccessRule(
        string ruleName,
        Func<string, bool> propertyNameMatches,
        Severity severity,
        string description,
        string suggestedFix,
        Func<RuleFileContext, ExpressionSyntax, int, bool>? extraCondition = null)
    {
        _ruleName = ruleName;
        _propertyNameMatches = propertyNameMatches;
        _severity = severity;
        _description = description;
        _suggestedFix = suggestedFix;
        _extraCondition = extraCondition;
    }

    public IEnumerable<string> EscalatableRuleNames => Array.Empty<string>();

    public IEnumerable<RuleMatch> FindMatches(RuleFileContext context)
    {
        foreach (var node in context.Root.DescendantNodes())
        {
            if (node is not MemberAccessExpressionSyntax and not MemberBindingExpressionSyntax)
            {
                continue;
            }

            var access = (ExpressionSyntax)node;

            var name = CodeReviewSyntaxTree.GetMemberName(access);
            if (name is null || !_propertyNameMatches(name.Identifier.Text))
            {
                continue;
            }

            var target = CodeReviewSyntaxTree.GetMemberTarget(access);
            var entityName = target is null ? null : CodeReviewSyntaxTree.GetMemberName(target);
            if (entityName is null || !context.EntityCatalog.Names.Contains(entityName.Identifier.Text) ||
                EntityCatalog.NonLazyLoadedReferenceNames.Contains(entityName.Identifier.Text))
            {
                continue;
            }

            var line = CodeReviewSyntaxTree.StartLine(access);
            if (_extraCondition is not null && !_extraCondition(context, access, line))
            {
                continue;
            }

            yield return new RuleMatch(_ruleName, line, _severity, _description, _suggestedFix);
        }
    }
}
