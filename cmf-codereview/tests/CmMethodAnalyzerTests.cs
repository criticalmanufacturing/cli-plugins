using Cmf.Cli.Plugin.CodeReview;
using Xunit;

namespace codereview.tests;

public class CmMethodAnalyzerTests
{
    private static readonly string[] Snippet =
    {
        "public class Foo",                       // 1
        "{",                                       // 2
        "    public void MethodA()",               // 3
        "    {",                                   // 4
        "        if (true)",                       // 5
        "        {",                               // 6
        "            DoSomething();",              // 7
        "        }",                               // 8
        "    }",                                   // 9
        "",                                         // 10
        "    private int MethodB(int x)",           // 11
        "    {",                                   // 12
        "        return x + 1;",                    // 13
        "    }",                                   // 14
        "",                                         // 15
        "    public static string MethodC()",       // 16
        "    {",                                   // 17
        "        return \"c\";",                    // 18
        "    }",                                   // 19
        "}"                                          // 20
    };

    [Fact]
    public void GetMethodRanges_FindsAllThreeMethodsWithCorrectRanges()
    {
        var ranges = CmMethodAnalyzer.GetMethodRanges(Snippet);

        Assert.Equal(3, ranges.Count);

        // Note: StartLine/StartDepth refer to the line of the opening '{', not the signature line.
        var methodA = Assert.Single(ranges, r => r.MethodName == "MethodA");
        Assert.Equal(4, methodA.StartLine);
        Assert.Equal(9, methodA.EndLine);

        var methodB = Assert.Single(ranges, r => r.MethodName == "MethodB");
        Assert.Equal(12, methodB.StartLine);
        Assert.Equal(14, methodB.EndLine);

        var methodC = Assert.Single(ranges, r => r.MethodName == "MethodC");
        Assert.Equal(17, methodC.StartLine);
        Assert.Equal(19, methodC.EndLine);
    }

    [Fact]
    public void GetMethodNameForLine_ResolvesLinesInsideEachMethodBody()
    {
        var ranges = CmMethodAnalyzer.GetMethodRanges(Snippet);

        Assert.Equal("MethodA", CmMethodAnalyzer.GetMethodNameForLine(ranges, 7));
        Assert.Equal("MethodB", CmMethodAnalyzer.GetMethodNameForLine(ranges, 13));
        Assert.Equal("MethodC", CmMethodAnalyzer.GetMethodNameForLine(ranges, 18));
    }

    [Fact]
    public void GetMethodNameForLine_LineOutsideAnyMethod_ReturnsNull()
    {
        var ranges = CmMethodAnalyzer.GetMethodRanges(Snippet);

        Assert.Null(CmMethodAnalyzer.GetMethodNameForLine(ranges, 1));
        Assert.Null(CmMethodAnalyzer.GetMethodNameForLine(ranges, 10));
        Assert.Null(CmMethodAnalyzer.GetMethodNameForLine(ranges, 20));
    }

    [Fact]
    public void GetMethodNameForLine_InnermostMethodWins_ForNestedRange()
    {
        // MethodA's body contains an `if` block, but that block is not itself a method,
        // so line 7 should still resolve to MethodA (the only real method range that contains it).
        var ranges = CmMethodAnalyzer.GetMethodRanges(Snippet);

        Assert.Equal("MethodA", CmMethodAnalyzer.GetMethodNameForLine(ranges, 6));
    }

    [Theory]
    [InlineData("if (x > 0)")]
    [InlineData("for (int i = 0; i < 10; i++)")]
    [InlineData("foreach (var i in items)")]
    [InlineData("while (true)")]
    [InlineData("switch (x)")]
    [InlineData("catch (Exception ex)")]
    [InlineData("using (var x = Foo())")]
    [InlineData("lock (this)")]
    public void GetMethodRanges_ReservedControlKeywords_AreNotTreatedAsMethodNames(string controlLine)
    {
        var lines = new[]
        {
            "void M()",
            "{",
            controlLine,
            "{",
            "    Body();",
            "}",
            "}"
        };

        var ranges = CmMethodAnalyzer.GetMethodRanges(lines);

        Assert.Single(ranges);
        Assert.Equal("M", ranges[0].MethodName);
    }

    [Fact]
    public void GetMethodRanges_EmptyInput_ReturnsEmptyList()
    {
        var ranges = CmMethodAnalyzer.GetMethodRanges(Array.Empty<string>());

        Assert.Empty(ranges);
    }

    [Fact]
    public void GetMethodRanges_LocalFunction_GetsItsOwnRangeDistinctFromEnclosingMethod()
    {
        var lines = new[]
        {
            "public class Foo",              // 1
            "{",                              // 2
            "    public void Outer()",        // 3
            "    {",                          // 4
            "        Inner();",               // 5
            "",                                // 6
            "        void Inner()",           // 7
            "        {",                      // 8
            "            DoWork();",          // 9
            "        }",                      // 10
            "    }",                          // 11
            "}"                                // 12
        };

        var ranges = CmMethodAnalyzer.GetMethodRanges(lines);

        Assert.Equal(2, ranges.Count);
        Assert.Equal("Outer", CmMethodAnalyzer.GetMethodNameForLine(ranges, 5));
        Assert.Equal("Inner", CmMethodAnalyzer.GetMethodNameForLine(ranges, 9));
    }

    [Fact]
    public void GetMethodRanges_ExpressionBodiedMethod_GetsARange()
    {
        // Regression test: the previous regex/brace-counting implementation only ever created a
        // range when it saw an opening '{', so an expression-bodied method ("=> ...;") was never
        // captured at all, and its pending method name could leak forward onto the next method's
        // opening brace instead.
        var lines = new[]
        {
            "public class Foo",                   // 1
            "{",                                    // 2
            "    public int Square(int x) => x * x;", // 3
            "",                                       // 4
            "    public void Other()",              // 5
            "    {",                                // 6
            "        DoWork();",                    // 7
            "    }",                                // 8
            "}"                                       // 9
        };

        var ranges = CmMethodAnalyzer.GetMethodRanges(lines);

        Assert.Equal(2, ranges.Count);
        Assert.Equal("Square", CmMethodAnalyzer.GetMethodNameForLine(ranges, 3));
        Assert.Equal("Other", CmMethodAnalyzer.GetMethodNameForLine(ranges, 7));
    }
}
