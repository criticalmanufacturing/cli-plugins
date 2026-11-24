namespace Cmf.Cli.Plugin.CodeReview.Models;

/// <summary>Opt-in findings envelope. A null summary means it was not recorded, unlike an empty summary.</summary>
public class FindingsFile
{
    public required List<Finding> Findings { get; init; }
    public List<ActionGroupSummary>? ActionGroupSummaries { get; init; }
}
