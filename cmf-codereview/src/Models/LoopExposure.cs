namespace Cmf.Cli.Plugin.CodeReview.Models;

public class LoopExposureHop
{
    public required string CallerMethodName { get; init; }
    public required string CallerRelativePath { get; init; }
    public int CallerLine { get; init; }
}

/// <summary>
/// Result of walking the caller chain of a method (up to a bounded depth) looking
/// for a call site that is itself inside a loop. Non-null only when such a loop was found.
/// </summary>
public class LoopExposure
{
    public const string CalledFromLoopKind = "CalledFromLoop";

    public required string Kind { get; init; }
    public required string LoopLocation { get; init; }
    public required string LoopMethod { get; init; }
    public int Depth { get; init; }
    public required List<LoopExposureHop> Path { get; init; }
}
