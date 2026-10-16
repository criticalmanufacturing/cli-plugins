using Cmf.Cli.Plugin.CodeReview.Models;
using Cmf.Cli.Plugin.CodeReview.Enums;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cmf.Cli.Plugin.CodeReview.Rules;

/// <summary>A direct Config.GetConfig(...) call, bypassing the cache-first TryGetConfig pattern.</summary>
public class ConfigGetConfigRule : IReviewRule
{
    public IEnumerable<string> EscalatableRuleNames => Array.Empty<string>();

    public IEnumerable<RuleMatch> FindMatches(RuleFileContext context)
    {
        foreach (var invocation in context.Root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax
                {
                    Expression: IdentifierNameSyntax { Identifier.Text: "Config" },
                    Name.Identifier.Text: "GetConfig"
                })
            {
                continue;
            }

            yield return new RuleMatch("ConfigGetConfig", CodeReviewSyntaxTree.StartLine(invocation), Severity.Medium,
                "Direct Config.GetConfig call bypasses the cache-first pattern.",
                "Prefer a project-level config wrapper service's TryGetConfigValue, or Config.TryGetConfig, depending on the layer.");
        }
    }
}

/// <summary>A GetConfig-style call that isn't TryGetConfig and isn't the already-covered Config.GetConfig case.</summary>
public class GetConfigInsteadOfTryGetConfigRule : IReviewRule
{
    public IEnumerable<string> EscalatableRuleNames => Array.Empty<string>();

    public IEnumerable<RuleMatch> FindMatches(RuleFileContext context)
    {
        foreach (var invocation in context.Root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var methodName = invocation.Expression switch
            {
                MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.Text,
                IdentifierNameSyntax identifier => identifier.Identifier.Text,
                _ => null
            };

            if (methodName != "GetConfig")
            {
                continue;
            }

            if (invocation.Expression is MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.Text: "Config" } })
            {
                continue; // that specific shape is ConfigGetConfigRule's, not this one's, to avoid double-reporting the same call
            }

            yield return new RuleMatch("GetConfigInsteadOfTryGetConfig", CodeReviewSyntaxTree.StartLine(invocation), Severity.Medium,
                "GetConfig-style access may skip the cache-first pattern.",
                "Confirm whether this should use TryGetConfig or an existing wrapper service before keeping the direct call.");
        }
    }
}

/// <summary>A local IConfig variable (from a declaration or Config.TryGetConfig(..., out var)) that's both assigned a new Value and Saved -- i.e. written from code, not just read.</summary>
public class ConfigWriteInHotPathRule : IReviewRule
{
    public IEnumerable<string> EscalatableRuleNames => Array.Empty<string>();

    public IEnumerable<RuleMatch> FindMatches(RuleFileContext context)
    {
        if (context.ConfigWriteVarNames.Count == 0)
        {
            yield break;
        }

        foreach (var invocation in context.Root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Save", Expression: IdentifierNameSyntax identifier } ||
                !context.ConfigWriteVarNames.Contains(identifier.Identifier.Text))
            {
                continue;
            }

            yield return new RuleMatch("ConfigWriteInHotPath", CodeReviewSyntaxTree.StartLine(invocation), Severity.High,
                $"Config entry '{identifier.Identifier.Text}' is written (Value assignment + Save) from code, not just read.",
                "Config entries should be rarely changed; store frequently-changing/counter-like values in an entity attribute, generic table, or cache instead of a Config entry.");
        }
    }
}
