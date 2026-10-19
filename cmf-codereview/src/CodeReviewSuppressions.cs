namespace Cmf.Cli.Plugin.CodeReview;

/// <summary>
/// Inline finding suppression via source comments, checked per flagged line:
/// <c>// codereview-disable-line</c> (on the flagged line) and
/// <c>// codereview-disable-next-line</c> (on the line above), each optionally followed by one or
/// more rule names (comma/space separated) to suppress only those rules — bare markers suppress
/// every rule on that line.
/// </summary>
public static class CodeReviewSuppressions
{
    private const string DisableLineMarker = "codereview-disable-line";
    private const string DisableNextLineMarker = "codereview-disable-next-line";

    public static bool IsSuppressed(IReadOnlyList<string> lines, int lineNumber, string ruleName)
    {
        // Check the flagged line for a same-line marker, and the previous line for a next-line marker.
        // Order matters: "codereview-disable-next-line" contains "codereview-disable-line" as a
        // substring, so each check must match its own marker exactly.
        return (TryGetMarkerRuleList(GetLine(lines, lineNumber), DisableLineMarker, out var sameLineRules) && Applies(sameLineRules, ruleName)) ||
               (TryGetMarkerRuleList(GetLine(lines, lineNumber - 1), DisableNextLineMarker, out var nextLineRules) && Applies(nextLineRules, ruleName));
    }

    private static string? GetLine(IReadOnlyList<string> lines, int lineNumber) =>
        lineNumber >= 1 && lineNumber <= lines.Count ? lines[lineNumber - 1] : null;

    private static bool TryGetMarkerRuleList(string? line, string marker, out string[] ruleNames)
    {
        ruleNames = Array.Empty<string>();
        if (line is null)
        {
            return false;
        }

        var commentStart = line.IndexOf("//", StringComparison.Ordinal);
        if (commentStart < 0)
        {
            return false;
        }

        var comment = line[commentStart..];
        var markerIndex = comment.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return false;
        }

        // Reject the -line marker matching inside a -next-line marker on the same line.
        if (marker == DisableLineMarker &&
            comment.Contains(DisableNextLineMarker, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        ruleNames = comment[(markerIndex + marker.Length)..]
            .Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);
        return true;
    }

    private static bool Applies(string[] ruleNames, string ruleName) =>
        ruleNames.Length == 0 || ruleNames.Contains(ruleName, StringComparer.OrdinalIgnoreCase);
}
