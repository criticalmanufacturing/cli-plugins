namespace Cmf.Cli.Plugin.CodeReview.Models;

/// <summary>
/// Records a code-level call site that invokes a DEE action by name
/// (via <c>ExecuteDeeAction("Name", ...)</c> or <c>action.Load("Name")</c>).
/// </summary>
public class DeeInvocationRecord
{
    public required string DeeName { get; init; }
    public required string CallerRelativePath { get; init; }
    public int CallerLine { get; init; }
    public string? CallerMethodName { get; init; }
}

/// <summary>
/// Records the first DEE call site found that invokes an Orchestration method through any
/// identifier ending in "Orchestration" (e.g. <c>_projectOrchestration.MethodName(...)</c> —
/// the proxy field name varies per project).
/// </summary>
public class OrchestrationCallSite
{
    public required string CallerRelativePath { get; init; }
    public int CallerLine { get; init; }
}
