using Cmf.Cli.Plugin.CodeReview.Models;
using Cmf.Cli.Plugin.CodeReview.Enums;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cmf.Cli.Plugin.CodeReview.Rules;

/// <summary>Direct construction of a known entity type instead of going through an entity factory.</summary>
public class DirectEntityInstantiationRule : IReviewRule
{
    public IEnumerable<string> EscalatableRuleNames => Array.Empty<string>();

    public IEnumerable<RuleMatch> FindMatches(RuleFileContext context)
    {
        foreach (var creation in context.Root.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            var typeName = creation.Type is null ? null : CodeReviewSyntaxTree.LastIdentifierSegment(creation.Type.ToString());
            if (typeName is null || !context.EntityCatalog.Names.Contains(typeName) ||
                EntityCatalog.IdiomaticDirectInstantiationNames.Contains(typeName))
            {
                continue;
            }

            yield return new RuleMatch("DirectEntityInstantiation", CodeReviewSyntaxTree.StartLine(creation), Severity.Medium,
                "Entity object is instantiated directly.",
                "Use entityFactory.Create<TInterface>() instead of direct construction.");
        }
    }
}
