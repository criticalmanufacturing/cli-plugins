using Cmf.Cli.Plugin.CodeReview;
using Xunit;

namespace codereview.tests;

public class CmDeeAnalyzerTests : IDisposable
{
    private readonly string _tempRoot;

    public CmDeeAnalyzerTests()
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

    private string CreateDeeDir()
    {
        var packagesDir = Path.Combine(_tempRoot, "Cmf.Custom.Data", "Cmf.Custom.Data.Packages");
        Directory.CreateDirectory(packagesDir);
        File.WriteAllText(Path.Combine(packagesDir, "cmfpackage.json"), """
            { "packageType": "Data", "contentToPack": [ { "source": "DEEs/*", "target": "DeeRules", "contentType": "DEE" } ] }
            """);

        var deeDir = Path.Combine(packagesDir, "DEEs");
        Directory.CreateDirectory(deeDir);
        return deeDir;
    }

    private string CreateOrchestrationDir()
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
        return dir;
    }

    [Fact]
    public void GetDeeClassNames_OnlyCapturesClassesInheritingDeeDevBase()
    {
        var deeDir = CreateDeeDir();

        File.WriteAllText(Path.Combine(deeDir, "MyDee.cs"), """
            namespace Foo;
            public class MyDee : DeeDevBase
            {
            }
            """);

        File.WriteAllText(Path.Combine(deeDir, "NotADee.cs"), """
            namespace Foo;
            public class NotADee
            {
            }
            """);

        var info = CmDeeAnalyzer.GetDeeClassNames(_tempRoot);

        Assert.Contains("MyDee", info.Names);
        Assert.DoesNotContain("NotADee", info.Names);
        Assert.Single(info.Names);
    }

    [Fact]
    public void GetConstantStringMap_ExtractsConstStringDeclarations()
    {
        var file = Path.Combine(_tempRoot, "Constants.cs");
        File.WriteAllText(file, """
            public class Constants
            {
                const string X = "value";
                const string Y = "other";
            }
            """);

        var map = CmDeeAnalyzer.GetConstantStringMap(new[] { file });

        Assert.Equal("value", map.Global["X"]);
        Assert.Equal("other", map.Global["Y"]);
        Assert.Equal("value", map.PerFile[file]["X"]);
    }

    [Fact]
    public void ResolveDeeActionExpression_LiteralString_ReturnsLiteralValue()
    {
        var result = CmDeeAnalyzer.ResolveDeeActionExpression(
            "\"MyDeeName\"",
            new Dictionary<string, string>(),
            new Dictionary<string, string>());

        Assert.Equal("MyDeeName", result);
    }

    [Fact]
    public void ResolveDeeActionExpression_LocalConstantIdentifier_ResolvesFromLocalMap()
    {
        var local = new Dictionary<string, string> { ["DeeName"] = "ResolvedLocal" };
        var global = new Dictionary<string, string> { ["DeeName"] = "ResolvedGlobal" };

        var result = CmDeeAnalyzer.ResolveDeeActionExpression("DeeName", local, global);

        Assert.Equal("ResolvedLocal", result);
    }

    [Fact]
    public void ResolveDeeActionExpression_UnresolvableExpression_ReturnsNull()
    {
        var result = CmDeeAnalyzer.ResolveDeeActionExpression(
            "SomeMethod()",
            new Dictionary<string, string>(),
            new Dictionary<string, string>());

        Assert.Null(result);
    }

    [Fact]
    public void GetDeeInvocationMap_ExecuteDeeActionCallWrappedAcrossMultipleLines_IsStillDetected()
    {
        // Regression test: the previous per-line regex scan could never see a call whose arguments
        // wrap onto a following line (a real formatting style for long argument lists). Locating
        // the call via its syntax node instead of a single source line fixes this.
        var deeDir = CreateDeeDir();
        File.WriteAllText(Path.Combine(deeDir, "MyDee.cs"), """
            public class MyDee : DeeDevBase
            {
            }
            """);

        var orchestrationDir = CreateOrchestrationDir();
        File.WriteAllText(Path.Combine(orchestrationDir, "Caller.cs"), """
            public class Caller
            {
                public void Invoke()
                {
                    ExecuteDeeAction(
                        "MyDee",
                        someOtherArgument);
                }
            }
            """);

        var deeClassInfo = CmDeeAnalyzer.GetDeeClassNames(_tempRoot);
        var invocationMap = CmDeeAnalyzer.GetDeeInvocationMap(_tempRoot, deeClassInfo.Names);

        Assert.True(invocationMap.ContainsKey("MyDee"));
        var record = Assert.Single(invocationMap["MyDee"]);
        Assert.Equal("Invoke", record.CallerMethodName);
        Assert.Equal(5, record.CallerLine);
    }

    [Theory]
    [InlineData("_projectOrchestration")]
    [InlineData("_otherOrchestration")]
    [InlineData("Orchestration")]
    public void GetOrchestrationCallSitesInDees_MatchesAnyIdentifierEndingInOrchestration_NotJustProjectSpecificNames(string proxyIdentifier)
    {
        var deeDir = CreateDeeDir();

        File.WriteAllText(Path.Combine(deeDir, "MyDee.cs"), $$"""
            public class MyDee : DeeDevBase
            {
                public void Execute()
                {
                    {{proxyIdentifier}}.SomeOrchestrationMethod();
                }
            }
            """);

        var callSites = CmDeeAnalyzer.GetOrchestrationCallSitesInDees(_tempRoot);

        Assert.True(callSites.ContainsKey("SomeOrchestrationMethod"));
    }

    [Fact]
    public void GetDeeClassInfoAndOrchestrationCallSites_MatchesSeparateMethodsCombined()
    {
        // Perf consolidation regression: the combined single-walk method must produce exactly what
        // calling GetDeeClassNames + GetOrchestrationCallSitesInDees separately would have produced.
        var deeDir = CreateDeeDir();
        File.WriteAllText(Path.Combine(deeDir, "MyDee.cs"), """
            public class MyDee : DeeDevBase
            {
                public void Execute()
                {
                    _projectOrchestration.SomeOrchestrationMethod();
                }
            }
            """);

        var expectedClassInfo = CmDeeAnalyzer.GetDeeClassNames(_tempRoot);
        var expectedCallSites = CmDeeAnalyzer.GetOrchestrationCallSitesInDees(_tempRoot);

        var (actualClassInfo, actualCallSites) = CmDeeAnalyzer.GetDeeClassInfoAndOrchestrationCallSites(_tempRoot);

        Assert.Equal(expectedClassInfo.Names.OrderBy(n => n), actualClassInfo.Names.OrderBy(n => n));
        Assert.Equal(expectedClassInfo.FileByName, actualClassInfo.FileByName);
        Assert.Equal(expectedCallSites.Keys.OrderBy(k => k), actualCallSites.Keys.OrderBy(k => k));
        foreach (var key in expectedCallSites.Keys)
        {
            Assert.Equal(expectedCallSites[key].CallerRelativePath, actualCallSites[key].CallerRelativePath);
            Assert.Equal(expectedCallSites[key].CallerLine, actualCallSites[key].CallerLine);
        }
    }

    [Fact]
    public void GetConstantsAndDeeInvocationMap_MatchesSeparateMethodsCombined()
    {
        // Perf consolidation regression: the combined single-walk method must produce exactly what
        // calling GetConstantStringMap + GetDeeInvocationMap separately would have produced, including
        // resolving a global constant declared in a different file than the one that references it.
        var deeDir = CreateDeeDir();
        File.WriteAllText(Path.Combine(deeDir, "MyDee.cs"), """
            public class MyDee : DeeDevBase
            {
            }
            """);

        var orchestrationDir = CreateOrchestrationDir();
        File.WriteAllText(Path.Combine(orchestrationDir, "Constants.cs"), """
            public static class Constants
            {
                public const string MyDeeName = "MyDee";
            }
            """);
        File.WriteAllText(Path.Combine(orchestrationDir, "Caller.cs"), """
            public class Caller
            {
                public void Invoke()
                {
                    ExecuteDeeAction(MyDeeName, someOtherArgument);
                }
            }
            """);

        var deeClassInfo = CmDeeAnalyzer.GetDeeClassNames(_tempRoot);
        var expectedInvocationMap = CmDeeAnalyzer.GetDeeInvocationMap(_tempRoot, deeClassInfo.Names);

        var (_, actualInvocationMap) = CmDeeAnalyzer.GetConstantsAndDeeInvocationMap(_tempRoot, deeClassInfo.Names);

        Assert.Equal(expectedInvocationMap.Keys.OrderBy(k => k), actualInvocationMap.Keys.OrderBy(k => k));
        foreach (var key in expectedInvocationMap.Keys)
        {
            Assert.Equal(expectedInvocationMap[key].Count, actualInvocationMap[key].Count);
            for (var i = 0; i < expectedInvocationMap[key].Count; i++)
            {
                Assert.Equal(expectedInvocationMap[key][i].CallerRelativePath, actualInvocationMap[key][i].CallerRelativePath);
                Assert.Equal(expectedInvocationMap[key][i].CallerLine, actualInvocationMap[key][i].CallerLine);
                Assert.Equal(expectedInvocationMap[key][i].CallerMethodName, actualInvocationMap[key][i].CallerMethodName);
            }
        }
        // Sanity: the global-constant-in-a-different-file resolution actually fired, not just an empty match on both sides.
        Assert.True(actualInvocationMap.ContainsKey("MyDee"));
    }
}
