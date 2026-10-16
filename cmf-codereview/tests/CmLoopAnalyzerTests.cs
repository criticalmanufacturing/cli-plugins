using Cmf.Cli.Plugin.CodeReview;
using Xunit;

namespace codereview.tests;

public class CmLoopAnalyzerTests
{
    [Fact]
    public void GetLoopState_ForLoopWithBraces_MarksBodyLinesAtDepth1()
    {
        var lines = new[]
        {
            "void M()",
            "{",
            "    for (int i = 0; i < 10; i++)",
            "    {",
            "        DoWork(i);",
            "    }",
            "}"
        };

        var state = CmLoopAnalyzer.GetLoopState(lines);

        Assert.Contains(5, state.Lines);
        Assert.Equal(1, state.DepthByLine[5]);
        Assert.DoesNotContain(1, state.Lines);
        Assert.DoesNotContain(2, state.Lines);
        Assert.DoesNotContain(7, state.Lines);
    }

    [Fact]
    public void GetLoopState_ForeachLoopWithBraces_MarksBodyLines()
    {
        var lines = new[]
        {
            "foreach (var item in items)",
            "{",
            "    item.Load();",
            "}"
        };

        var state = CmLoopAnalyzer.GetLoopState(lines);

        Assert.Contains(3, state.Lines);
        Assert.Equal(1, state.DepthByLine[3]);
    }

    [Fact]
    public void GetLoopState_WhileLoopWithBraces_MarksBodyLines()
    {
        var lines = new[]
        {
            "while (condition)",
            "{",
            "    DoSomething();",
            "}"
        };

        var state = CmLoopAnalyzer.GetLoopState(lines);

        Assert.Contains(3, state.Lines);
    }

    [Fact]
    public void GetLoopState_NestedLoops_TracksDepthCorrectly()
    {
        var lines = new[]
        {
            "for (int i = 0; i < 10; i++)",     // 1
            "{",                                 // 2
            "    foreach (var item in items)",   // 3
            "    {",                             // 4
            "        item.Load();",              // 5 - depth 2
            "    }",                             // 6
            "    OuterOnly();",                  // 7 - depth 1
            "}"                                   // 8
        };

        var state = CmLoopAnalyzer.GetLoopState(lines);

        Assert.Equal(2, state.DepthByLine[5]);
        Assert.Equal(1, state.DepthByLine[7]);

        // Note: the closing brace of the outer loop is itself still evaluated while the loop is
        // considered "active" (the brace-depth pop happens after the line is classified), so it
        // is marked as a loop line too, at the depth the loop was at before closing.
        Assert.Contains(8, state.Lines);
        Assert.Equal(1, state.DepthByLine[8]);
    }

    [Fact]
    public void GetLoopState_SingleLineLoopBodyWithoutBraces_MarksNextLineOnly()
    {
        var lines = new[]
        {
            "for (int i = 0; i < 10; i++)",
            "    DoWork(i);",
            "AfterLoop();"
        };

        var state = CmLoopAnalyzer.GetLoopState(lines);

        Assert.Contains(2, state.Lines);
        Assert.Equal(1, state.DepthByLine[2]);
        Assert.DoesNotContain(3, state.Lines);
    }

    [Fact]
    public void GetLoopState_SingleLineLoopBody_SkipsCommentOnlyLineWhenFindingBody()
    {
        var lines = new[]
        {
            "for (int i = 0; i < 10; i++)",
            "    // a comment, not the body",
            "    DoWork(i);",
            "AfterLoop();"
        };

        var state = CmLoopAnalyzer.GetLoopState(lines);

        // The comment-only line must not be treated as the loop body.
        Assert.DoesNotContain(2, state.Lines);
        Assert.Contains(3, state.Lines);
        Assert.DoesNotContain(4, state.Lines);
    }

    [Fact]
    public void GetLoopState_CommentOnlyLineInsideBracedLoopBody_IsStillMarkedAsLoopLine()
    {
        // Once truly inside a brace-delimited loop body (activeLoop == true), every line
        // including comment-only ones is marked -- the comment-skipping behavior only matters
        // for locating the body of a brace-less single-line loop (see the SkipsCommentOnlyLine
        // test below).
        var lines = new[]
        {
            "for (int i = 0; i < 10; i++)",
            "{",
            "    // just a comment",
            "    DoWork(i);",
            "}"
        };

        var state = CmLoopAnalyzer.GetLoopState(lines);

        Assert.Contains(3, state.Lines);
        Assert.Contains(4, state.Lines);
    }

    [Fact]
    public void GetLoopState_LinqForEachCall_IsNotMisdetectedAsLoopKeyword()
    {
        // Regression test: `.ForEach(` is a LINQ method call, not a foreach loop statement.
        // The keyword regex must be case-sensitive so this doesn't corrupt brace-depth tracking.
        var lines = new[]
        {
            "void M()",
            "{",
            "    list.ForEach(x => x.DoThing());",
            "    NotInALoop();",
            "}"
        };

        var state = CmLoopAnalyzer.GetLoopState(lines);

        Assert.Empty(state.Lines);
        Assert.DoesNotContain(4, state.Lines);
    }

    [Fact]
    public void GetLoopState_LinqForEachInsideRealLoop_DoesNotAddExtraNestingDepth()
    {
        // `.ForEach(` inside a real loop body should still count as depth 1 (the real loop),
        // not be treated as an additional nested loop that would bump depth to 2.
        var lines = new[]
        {
            "for (int i = 0; i < 10; i++)",
            "{",
            "    list.ForEach(x => x.DoThing());",
            "    AfterForEach();",
            "}"
        };

        var state = CmLoopAnalyzer.GetLoopState(lines);

        Assert.Equal(1, state.DepthByLine[3]);
        Assert.Equal(1, state.DepthByLine[4]);
    }

    [Fact]
    public void GetLoopState_ForLoopHeaderWrappedAcrossMultipleLines_StillDetectsBodyCorrectly()
    {
        // Regression test: the previous per-line regex scan located the loop keyword on a single
        // line; a `for (...)` header wrapped across multiple lines (a real formatting style, e.g.
        // long condition expressions) was never a problem for the header itself since the keyword
        // regex only needed to match "for (" on its own line, but this locks in that a
        // syntax-tree-based detector handles it too, now that detection is node-based rather than
        // line-based.
        var lines = new[]
        {
            "for (int i = 0;",                 // 1
            "     i < someReallyLongCondition;", // 2
            "     i++)",                          // 3
            "{",                                   // 4
            "    DoWork(i);",                      // 5
            "}"                                     // 6
        };

        var state = CmLoopAnalyzer.GetLoopState(lines);

        Assert.Contains(5, state.Lines);
        Assert.Equal(1, state.DepthByLine[5]);
        Assert.DoesNotContain(1, state.Lines);
        Assert.DoesNotContain(2, state.Lines);
        Assert.DoesNotContain(3, state.Lines);
    }
}
