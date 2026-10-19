using Cmf.Cli.Plugin.CodeReview;
using Cmf.Cli.Plugin.CodeReview.Models;
using Xunit;

namespace codereview.tests;

public class CmLoopExposureFinderTests
{
    private static CallerRecord Caller(string method, bool isInLoop, string path = "file.cs", int line = 1) =>
        new()
        {
            CallerMethodName = method,
            CallerRelativePath = path,
            CallerLine = line,
            IsInLoop = isInLoop
        };

    [Fact]
    public void FindLoopExposure_MethodCalledDirectlyFromLoop_ReturnsExposureAtDepth1()
    {
        // B is called from A, and that call site is inside a loop.
        var graph = new MethodCallGraph();
        graph.CallersByMethod["B"] = new List<CallerRecord> { Caller("A", isInLoop: true) };

        var exposure = CmLoopExposureFinder.FindLoopExposure(graph, "B");

        Assert.NotNull(exposure);
        Assert.Equal(LoopExposure.CalledFromLoopKind, exposure!.Kind);
        Assert.Equal(1, exposure.Depth);
        Assert.Equal("A", exposure.LoopMethod);
    }

    [Fact]
    public void FindLoopExposure_TransitiveChainWithinMaxDepth_IsFlagged()
    {
        // Chain: C <- B <- A, and A's call to B happens inside a loop.
        // So calling C is transitively loop-exposed at depth 2.
        var graph = new MethodCallGraph();
        graph.CallersByMethod["C"] = new List<CallerRecord> { Caller("B", isInLoop: false) };
        graph.CallersByMethod["B"] = new List<CallerRecord> { Caller("A", isInLoop: true) };

        var exposure = CmLoopExposureFinder.FindLoopExposure(graph, "C");

        Assert.NotNull(exposure);
        Assert.Equal(2, exposure!.Depth);
        Assert.Equal("A", exposure.LoopMethod);
    }

    [Fact]
    public void FindLoopExposure_NoLoopAnywhereInChain_ReturnsNull()
    {
        var graph = new MethodCallGraph();
        graph.CallersByMethod["C"] = new List<CallerRecord> { Caller("B", isInLoop: false) };
        graph.CallersByMethod["B"] = new List<CallerRecord> { Caller("A", isInLoop: false) };

        var exposure = CmLoopExposureFinder.FindLoopExposure(graph, "C");

        Assert.Null(exposure);
    }

    [Fact]
    public void FindLoopExposure_LoopFourHopsAway_IsNotFlagged()
    {
        // Chain: E <- D <- C <- B <- A, with A's call to B inside a loop.
        // That loop is 4 hops away from E (E->D->C->B->A), beyond the default maxDepth of 3
        // (which only ever expands callers up to Depth 0,1,2, discovering loops at hop-depth <= 3),
        // so it must not be flagged.
        var graph = new MethodCallGraph();
        graph.CallersByMethod["E"] = new List<CallerRecord> { Caller("D", isInLoop: false) };
        graph.CallersByMethod["D"] = new List<CallerRecord> { Caller("C", isInLoop: false) };
        graph.CallersByMethod["C"] = new List<CallerRecord> { Caller("B", isInLoop: false) };
        graph.CallersByMethod["B"] = new List<CallerRecord> { Caller("A", isInLoop: true) };

        var exposure = CmLoopExposureFinder.FindLoopExposure(graph, "E");

        Assert.Null(exposure);
    }

    [Fact]
    public void FindLoopExposure_LoopExactlyThreeHopsAway_IsFlagged()
    {
        // Chain: D <- C <- B <- A, with A's call to B inside a loop -> 3 hops away from D,
        // which is within the default maxDepth of 3. Verifies the boundary case one hop closer
        // than the "four hops away" test above.
        var graph = new MethodCallGraph();
        graph.CallersByMethod["D"] = new List<CallerRecord> { Caller("C", isInLoop: false) };
        graph.CallersByMethod["C"] = new List<CallerRecord> { Caller("B", isInLoop: false) };
        graph.CallersByMethod["B"] = new List<CallerRecord> { Caller("A", isInLoop: true) };

        var exposure = CmLoopExposureFinder.FindLoopExposure(graph, "D", maxDepth: 3);

        Assert.NotNull(exposure);
        Assert.Equal(3, exposure!.Depth);
    }

    [Fact]
    public void FindLoopExposure_MethodWithNoCallers_ReturnsNull()
    {
        var graph = new MethodCallGraph();

        var exposure = CmLoopExposureFinder.FindLoopExposure(graph, "Orphan");

        Assert.Null(exposure);
    }

    [Fact]
    public void FormatChain_ProducesReadableSentenceWithAllHops()
    {
        var graph = new MethodCallGraph();
        graph.CallersByMethod["C"] = new List<CallerRecord> { Caller("B", isInLoop: false, path: "b.cs", line: 10) };
        graph.CallersByMethod["B"] = new List<CallerRecord> { Caller("A", isInLoop: true, path: "a.cs", line: 20) };

        var exposure = CmLoopExposureFinder.FindLoopExposure(graph, "C");
        Assert.NotNull(exposure);

        var chain = CmLoopExposureFinder.FormatChain("C", exposure!);

        Assert.Contains("'B' calls 'C'", chain);
        Assert.Contains("'A' calls 'B'", chain);
        Assert.Contains("in a loop", chain);
        Assert.Contains("a.cs:20", chain);
    }
}
