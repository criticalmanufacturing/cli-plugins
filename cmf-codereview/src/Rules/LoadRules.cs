using Cmf.Cli.Plugin.CodeReview.Models;
using Cmf.Cli.Plugin.CodeReview.Enums;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cmf.Cli.Plugin.CodeReview.Rules;

/// <summary>A Load/LoadRelations/LoadChildren call whose levelsToLoad argument is greater than 1.</summary>
public class LevelsToLoadGreaterThanOneRule : IReviewRule
{
    private static readonly HashSet<string> MethodNames = new(StringComparer.Ordinal) { "Load", "LoadRelations", "LoadChildren" };

    public IEnumerable<string> EscalatableRuleNames => Array.Empty<string>();

    public IEnumerable<RuleMatch> FindMatches(RuleFileContext context)
    {
        foreach (var invocation in context.Root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var name = CodeReviewSyntaxTree.GetMemberName(invocation.Expression);
            if (name is null || !MethodNames.Contains(name.Identifier.Text))
            {
                continue;
            }

            var argument = invocation.ArgumentList.Arguments.FirstOrDefault(a => a.NameColon?.Name.Identifier.Text == "levelsToLoad")
                ?? invocation.ArgumentList.Arguments.FirstOrDefault();

            if (argument?.Expression is not LiteralExpressionSyntax literal || !literal.IsKind(SyntaxKind.NumericLiteralExpression))
            {
                continue;
            }

            if (!int.TryParse(literal.Token.ValueText, out var level) || level <= 1)
            {
                continue;
            }

            var line = CodeReviewSyntaxTree.StartLine(invocation);
            var transitiveLoopNote = context.GetTransitiveLoopNote(context.GetMethodNameForLine(line));

            yield return new RuleMatch("LevelsToLoadGreaterThanOne", line, Severity.Critical,
                $"{name.Identifier.Text} uses levelsToLoad {level}.{transitiveLoopNote}",
                "Use the default one-level load and load deeper references only where they are actually needed.");
        }
    }
}

/// <summary>A LoadData() call with no filter arguments.</summary>
public class LoadDataWithoutFiltersRule : IReviewRule
{
    public IEnumerable<string> EscalatableRuleNames => Array.Empty<string>();

    public IEnumerable<RuleMatch> FindMatches(RuleFileContext context)
    {
        foreach (var invocation in context.Root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (CodeReviewSyntaxTree.GetMemberName(invocation.Expression)?.Identifier.Text != "LoadData")
            {
                continue;
            }

            if (invocation.ArgumentList.Arguments.Count > 0)
            {
                continue;
            }

            var line = CodeReviewSyntaxTree.StartLine(invocation);
            var transitiveLoopNote = context.GetTransitiveLoopNote(context.GetMethodNameForLine(line));

            yield return new RuleMatch("LoadDataWithoutFilters", line, Severity.Critical,
                $"LoadData is called without filters.{transitiveLoopNote}",
                "Use LoadData(filters) and constrain the table to the rows needed by the current flow.");
        }
    }
}
