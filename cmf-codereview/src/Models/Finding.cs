using Cmf.Cli.Plugin.CodeReview.Enums;

namespace Cmf.Cli.Plugin.CodeReview.Models;

public class Finding
{
    public required string FilePath { get; init; }
    public required string RelativePath { get; init; }
    public int LineNumber { get; init; }
    public Severity Severity { get; init; }
    public required string Rule { get; init; }
    public required string Description { get; init; }
    public required string Snippet { get; init; }
    public required string SuggestedFix { get; init; }
    public string? MethodName { get; init; }
    public string? ServiceEndpoint { get; init; }
    public string? ServiceEndpointSource { get; init; }
    public CrossReference? CrossReference { get; init; }
    public LoopExposure? LoopExposure { get; init; }

    /// <summary>Why severity was raised above the rule's base severity; null for findings at base severity.</summary>
    public Escalation? Escalation { get; init; }
}
