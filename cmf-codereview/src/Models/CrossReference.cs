namespace Cmf.Cli.Plugin.CodeReview.Models;

/// <summary>
/// Describes how a finding's enclosing method relates to the DEE &lt;-&gt; Orchestration
/// call boundary: whether it is invoked by a DEE, invokes/represents a DEE invoked by
/// Orchestration/Services code, or has no discoverable code-level caller at all.
/// </summary>
public class CrossReference
{
    public const string CalledByOrchestrationKind = "CalledByOrchestration";
    public const string CalledByDeeKind = "CalledByDee";
    public const string NoCallerFoundKind = "NoCallerFound";

    public required string Kind { get; init; }
    public string? Location { get; init; }
    public string? Method { get; init; }
    public string? Endpoint { get; init; }
    public int ExtraCount { get; init; }
}
