using Cmf.Cli.Plugin.CodeReview.Models;
using Cmf.Cli.Plugin.CodeReview.Enums;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cmf.Cli.Plugin.CodeReview.Rules;

/// <summary>Extension-method code (a file under Extensions/, containing at least one `this`-parameter method) that references ApplicationContext.</summary>
public class ApplicationContextInExtensionRule : IReviewRule
{
    public IEnumerable<string> EscalatableRuleNames => Array.Empty<string>();

    public IEnumerable<RuleMatch> FindMatches(RuleFileContext context)
    {
        if (!context.IsExtensionFile || !context.ContainsExtensionMethod)
        {
            yield break;
        }

        var reportedLines = new HashSet<int>();

        foreach (var identifier in context.Root.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            if (identifier.Identifier.Text != "ApplicationContext")
            {
                continue;
            }

            var line = CodeReviewSyntaxTree.StartLine(identifier);
            if (!reportedLines.Add(line))
            {
                continue; // at most one finding per line, even if referenced more than once on it
            }

            yield return new RuleMatch("ApplicationContextInExtension", line, Severity.Medium,
                "Extension code references ApplicationContext.",
                "Pass required services into the caller or move the logic into an injectable service.");
        }
    }
}
