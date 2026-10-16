using Cmf.Cli.Plugin.CodeReview;
using Xunit;

namespace codereview.tests;

public class CmCallGraphBuilderTests : IDisposable
{
    private readonly string _tempRoot;

    public CmCallGraphBuilderTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "codereview-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private string CreateOrchestrationFile(string fileName, string content)
    {
        var businessDir = Path.Combine(_tempRoot, "Cmf.Custom.Business");
        var manifestPath = Path.Combine(businessDir, "cmfpackage.json");
        if (!File.Exists(manifestPath))
        {
            Directory.CreateDirectory(businessDir);
            File.WriteAllText(manifestPath, """{ "packageType": "Business" }""");
        }

        var dir = Path.Combine(businessDir, "Cmf.Custom.Orchestration");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void BuildCallGraph_MethodACallingMethodB_RecordsCallerEdge()
    {
        CreateOrchestrationFile("Foo.cs", """
            public class Foo
            {
                public void A()
                {
                    this.B();
                }

                public void B()
                {
                }
            }
            """);

        var graph = CmCallGraphBuilder.BuildCallGraph(_tempRoot);

        Assert.Contains("A", graph.DefinedMethodNames);
        Assert.Contains("B", graph.DefinedMethodNames);
        Assert.True(graph.CallersByMethod.ContainsKey("B"));

        var caller = Assert.Single(graph.CallersByMethod["B"]);
        Assert.Equal("A", caller.CallerMethodName);
        Assert.False(caller.IsInLoop);
    }

    [Fact]
    public void BuildCallGraph_CallInsideLoop_MarksCallerRecordAsInLoop()
    {
        CreateOrchestrationFile("Foo.cs", """
            public class Foo
            {
                public void A()
                {
                    for (int i = 0; i < 10; i++)
                    {
                        this.B();
                    }
                }

                public void B()
                {
                }
            }
            """);

        var graph = CmCallGraphBuilder.BuildCallGraph(_tempRoot);

        var caller = Assert.Single(graph.CallersByMethod["B"]);
        Assert.True(caller.IsInLoop);
    }

    [Fact]
    public void BuildCallGraph_SelfRecursiveCall_IsExcluded()
    {
        CreateOrchestrationFile("Foo.cs", """
            public class Foo
            {
                public void A()
                {
                    this.A();
                }
            }
            """);

        var graph = CmCallGraphBuilder.BuildCallGraph(_tempRoot);

        Assert.False(graph.CallersByMethod.ContainsKey("A"));
    }

    [Fact]
    public void BuildCallGraph_CallLookingTextInsideCommentOrStringLiteral_IsNotMisdetectedAsARealCall()
    {
        // Regression test: the previous per-line regex scanned raw line text, so a call-shaped
        // pattern inside a comment or a string literal could be misdetected as a real call site.
        // Locating calls via InvocationExpressionSyntax nodes means only real invocations count.
        CreateOrchestrationFile("Foo.cs", """
            public class Foo
            {
                public void A()
                {
                    // this.B();
                    var s = "this.B();";
                }

                public void B()
                {
                }
            }
            """);

        var graph = CmCallGraphBuilder.BuildCallGraph(_tempRoot);

        Assert.False(graph.CallersByMethod.ContainsKey("B"));
    }

    [Fact]
    public void BuildCallGraph_MethodNameDefinedInMultipleTypes_GetsNoCallerEdges()
    {
        // The graph is name-based: with two unrelated classes both defining Sync, binding a call
        // site to either one fabricates cross-method loop chains (LoopInducedByCaller FPs).
        CreateOrchestrationFile("Foo.cs", """
            public class WidgetService { public void Sync() { } }
            public class GadgetService { public void Sync() { } }
            public class Caller
            {
                public void Run()
                {
                    foreach (var w in widgets)
                    {
                        _widgetService.Sync();
                    }
                }
            }
            """);

        var graph = CmCallGraphBuilder.BuildCallGraph(_tempRoot);

        Assert.Contains("Sync", graph.AmbiguousMethodNames);
        Assert.False(graph.CallersByMethod.ContainsKey("Sync"));
    }

    [Fact]
    public void BuildCallGraph_MemberAccessCallMatchingOnlyALocalFunctionName_GetsNoCallerEdge()
    {
        // Regression for the real Micron-1 case: a platform call
        // _exceptionOrchestration.OpenProtocolInstance(...) was misbound to a same-named DEE-LOCAL
        // function, fabricating a caller-loop chain. A receiver.Method() call can never target a
        // local function (local functions are called by bare name), so the name must not be a target.
        CreateOrchestrationFile("Foo.cs", """
            public class Foo
            {
                public void Caller()
                {
                    foreach (var x in items)
                    {
                        _exceptionOrchestration.OpenProtocolInstance(input);
                    }
                }

                public void Host()
                {
                    void OpenProtocolInstance(string p)
                    {
                        material.Load();
                    }

                    OpenProtocolInstance("x");
                }
            }
            """);

        var graph = CmCallGraphBuilder.BuildCallGraph(_tempRoot);

        Assert.DoesNotContain("OpenProtocolInstance", graph.DefinedMethodNames);
        Assert.False(graph.CallersByMethod.ContainsKey("OpenProtocolInstance"));
    }

    [Fact]
    public void BuildCallGraph_NameDefinedAsRegularMethodInOneTypeAndLocalFunctionInAnother_BindsToTheRegularMethodOnly()
    {
        // The local function must not make the name "ambiguous" -- only real method declarations
        // count toward origin, so the genuine regular-method edge is still recorded.
        CreateOrchestrationFile("Foo.cs", """
            public class Helper
            {
                public void Sync() { }
            }
            public class Caller
            {
                public void Run()
                {
                    foreach (var x in items)
                    {
                        _helper.Sync();
                    }
                }
                public void Other()
                {
                    void Sync() { }
                    Sync();
                }
            }
            """);

        var graph = CmCallGraphBuilder.BuildCallGraph(_tempRoot);

        Assert.DoesNotContain("Sync", graph.AmbiguousMethodNames);
        var caller = Assert.Single(graph.CallersByMethod["Sync"]);
        Assert.True(caller.IsInLoop);
    }

    [Fact]
    public void BuildCallGraph_PartialClassAcrossFiles_IsNotTreatedAsAmbiguous()
    {
        CreateOrchestrationFile("FooPart1.cs", """
            public partial class Foo { public void Helper() { } }
            """);
        CreateOrchestrationFile("FooPart2.cs", """
            public partial class Foo
            {
                public void Run()
                {
                    foreach (var x in items)
                    {
                        this.Helper();
                    }
                }
            }
            """);

        var graph = CmCallGraphBuilder.BuildCallGraph(_tempRoot);

        Assert.DoesNotContain("Helper", graph.AmbiguousMethodNames);
        var caller = Assert.Single(graph.CallersByMethod["Helper"]);
        Assert.True(caller.IsInLoop);
    }
}
