namespace Cmf.Cli.Plugin.CodeReview.Models;

public class CallerRecord
{
    public required string CallerMethodName { get; init; }
    public required string CallerRelativePath { get; init; }
    public int CallerLine { get; init; }
    public bool IsInLoop { get; init; }
}

public class MethodCallGraph
{
    public HashSet<string> DefinedMethodNames { get; } = new();

    /// <summary>
    /// Method names defined in more than one distinct type across the scanned scope. The graph is
    /// name-based (no semantic model), so a call site <c>x.Foo()</c> can't be attributed to THE
    /// <c>Foo</c> when several unrelated classes define one -- binding it anyway invented
    /// caller-loop chains between unrelated code (LoopInducedByCaller false positives). Ambiguous
    /// names get no caller edges at all: missing a real cross-method loop is the accepted cost of
    /// never reporting a fabricated one.
    /// </summary>
    public HashSet<string> AmbiguousMethodNames { get; } = new();

    public Dictionary<string, List<CallerRecord>> CallersByMethod { get; } = new();
}
