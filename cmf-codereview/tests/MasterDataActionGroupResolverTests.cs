using System.Text.Json;
using Cmf.Cli.Plugin.CodeReview;
using Cmf.Cli.Plugin.CodeReview.Enums;
using Cmf.Cli.Plugin.CodeReview.Models;
using Xunit;

namespace codereview.tests;

internal sealed class ActionGroupFixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "codereview-wiring-" + Guid.NewGuid());

    public ActionGroupFixture() => Directory.CreateDirectory(Root);

    public string Write(string relativePath, string content)
    {
        var path = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public string AddPackage(string name = "Cmf.Sample.Data")
    {
        Write($"{name}/cmfpackage.json", """
            { "packageType": "Data", "contentToPack": [
              { "contentType": "DEE", "source": "DEEs/*" },
              { "contentType": "MasterData", "source": "Wiring/$(version)/*" }
            ] }
            """);
        Directory.CreateDirectory(Path.Combine(Root, name, "DEEs"));
        Directory.CreateDirectory(Path.Combine(Root, name, "Wiring"));
        return $"{name}/Wiring";
    }

    public static string Orders(params (string Dee, string Group)[] pairs) => JsonSerializer.Serialize(new
    {
        DEEActionOrder = pairs.Select((pair, index) => (pair, index))
            .ToDictionary(item => item.index.ToString(), item => new { Action = item.pair.Dee, ActionGroup = item.pair.Group })
    });

    public void Dispose() => Directory.Delete(Root, recursive: true);
}

public class MasterDataActionGroupResolverTests : IDisposable
{
    private readonly ActionGroupFixture _fixture = new();
    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void ThreeWiringsInOneFile_EachGroupCountsTheSameDee()
    {
        var root = _fixture.AddPackage();
        _fixture.Write($"{root}/2.0.0/orders.json", ActionGroupFixture.Orders(
            ("SharedDee", "GroupC"), ("SharedDee", "GroupA"), ("SharedDee", "GroupB"), ("SharedDee", "GroupA")));

        var map = MasterDataActionGroupResolver.GetActionGroupMap(_fixture.Root);
        Assert.Equal(new[] { "GroupA", "GroupB", "GroupC" }, map["SharedDee"].Select(info => info.ActionGroup));
        var summaries = MasterDataActionGroupResolver.Summarize(map);
        Assert.Equal(3, summaries.Count);
        Assert.All(summaries, summary =>
        {
            Assert.Equal(1, summary.DeeCount);
            Assert.Equal(ActionGroupRiskLevel.Low, summary.RiskLevel);
            Assert.Equal(new[] { "SharedDee" }, summary.Dees);
        });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RepeatedPair_HighestNumericVersionWinsAcrossRootsInEitherOrder(bool reverse, bool highInFirstRoot)
    {
        var roots = new[] { _fixture.AddPackage("A.Data"), _fixture.AddPackage("Z.Data") };
        var high = roots[highInFirstRoot ? 0 : 1];
        var low = roots[highInFirstRoot ? 1 : 0];
        _fixture.Write($"{high}/2.10.0/a.json", ActionGroupFixture.Orders(("Dee", "GroupA")));
        _fixture.Write($"{low}/2.9.0/z.json", ActionGroupFixture.Orders(("Dee", "GroupA"), ("Dee", "GroupB")));
        if (reverse) Array.Reverse(roots);

        var map = MasterDataActionGroupResolver.GetActionGroupMap(_fixture.Root, roots);
        Assert.Equal(2, map["Dee"].Count);
        Assert.Equal($"{high}/2.10.0/a.json", map["Dee"].Single(info => info.ActionGroup == "GroupA").SourceLocation);
        Assert.Equal($"{low}/2.9.0/z.json", map["Dee"].Single(info => info.ActionGroup == "GroupB").SourceLocation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EqualVersions_OrdinalGreatestPathWinsRegardlessOfDiscoveryAndCreationOrder(bool reverse)
    {
        var roots = new[] { _fixture.AddPackage("A.Data"), _fixture.AddPackage("Z.Data") };
        var paths = new[] { $"{roots[0]}/2.10.0/z.json", $"{roots[1]}/2.10/a.json", $"{roots[1]}/2.10/z.json" };
        foreach (var path in reverse ? paths.Reverse() : paths)
            _fixture.Write(path, ActionGroupFixture.Orders(("Dee", "Group")));
        if (reverse) Array.Reverse(roots);
        var map = MasterDataActionGroupResolver.GetActionGroupMap(_fixture.Root, roots.Concat(roots));
        Assert.Equal(paths[2], Assert.Single(map["Dee"]).SourceLocation);
        Assert.Equal(JsonSerializer.Serialize(map), JsonSerializer.Serialize(MasterDataActionGroupResolver.GetActionGroupMap(_fixture.Root, roots.Reverse())));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("{\"DEEActionOrder\":[]}")]
    [InlineData("{\"DEEActionOrder\":{\"0\":null,\"1\":{\"Action\":42,\"ActionGroup\":\"A\"},\"2\":{\"Action\":\" \",\"ActionGroup\":\"A\"}}}")]
    public void MalformedOrIrrelevantFiles_DoNotPreventOtherWiringFromResolving(string content)
    {
        var root = _fixture.AddPackage();
        _fixture.Write($"{root}/2.0.0/bad.json", content);
        _fixture.Write($"{root}/2.0.0/good.json", ActionGroupFixture.Orders(("Dee", "Group")));
        _fixture.Write($"{root}/latest/ignored.json", ActionGroupFixture.Orders(("IgnoredDee", "Other")));
        var map = MasterDataActionGroupResolver.GetActionGroupMap(_fixture.Root);
        Assert.Single(map);
        Assert.Equal("Group", Assert.Single(map["Dee"]).ActionGroup);
    }

    [Fact]
    public void Discovery_UsesOnlyMasterDataEntriesInDataManifestsIncludingMasterDataOnlyPackages()
    {
        _fixture.Write("OnlyData/cmfpackage.json", """{"packageType":"Data","contentToPack":[{"contentType":"MasterData","source":"CustomData/$(version)/*"}]}""");
        _fixture.Write("OnlyData/CustomData/1.0/a.json", ActionGroupFixture.Orders(("Dee", "Group")));
        _fixture.Write("Business/cmfpackage.json", """{"packageType":"Business","contentToPack":[{"contentType":"MasterData","source":"MasterData/$(version)/*"}]}""");
        _fixture.Write("Business/MasterData/9.0/a.json", ActionGroupFixture.Orders(("Wrong", "Group")));
        _fixture.AddPackage("EmptyData");
        Assert.Equal(new[] { "EmptyData/Wiring", "OnlyData/CustomData" },
            CodeReviewPaths.DiscoverMasterDataRoots(_fixture.Root).Select(path => path.Replace('\\', '/')));
        Assert.Equal(new[] { "Dee" }, MasterDataActionGroupResolver.GetActionGroupMap(_fixture.Root).Keys);
    }

    [Fact]
    public void NoMasterDataEntries_ReturnsEmptyMap()
    {
        _fixture.Write("cmfpackage.json", """{"packageType":"Data","contentToPack":[]}""");
        Assert.Empty(MasterDataActionGroupResolver.GetActionGroupMap(_fixture.Root));
    }

    [Theory]
    [InlineData(1, ActionGroupRiskLevel.Low)]
    [InlineData(2, ActionGroupRiskLevel.Medium)]
    [InlineData(3, ActionGroupRiskLevel.High)]
    [InlineData(4, ActionGroupRiskLevel.Critical)]
    [InlineData(5, ActionGroupRiskLevel.Critical)]
    public void RiskCountsDistinctDeesUnderEveryGroup(int count, ActionGroupRiskLevel expected)
    {
        var root = _fixture.AddPackage();
        var pairs = Enumerable.Range(1, count).Select(index => ($"Dee{index}", "Shared.Pre"))
            .Concat(new[] { ("Dee1", "Shared.Pre"), ("Dee1", "Shared.Post") }).ToArray();
        _fixture.Write($"{root}/1.0/a.json", ActionGroupFixture.Orders(pairs));
        var summaries = MasterDataActionGroupResolver.Summarize(MasterDataActionGroupResolver.GetActionGroupMap(_fixture.Root));
        var pre = summaries.Single(summary => summary.ActionGroup == "Shared.Pre");
        Assert.Equal(count, pre.DeeCount);
        Assert.Equal(expected, pre.RiskLevel);
        Assert.Equal(1, summaries.Single(summary => summary.ActionGroup == "Shared.Post").DeeCount);
    }

    [Fact]
    public void Endpoints_AreResolvedForEveryWiringThroughTransitiveCallersAndCycles()
    {
        var graph = new MethodCallGraph();
        static CallerRecord Caller(string method) => new() { CallerMethodName = method, CallerRelativePath = "file.cs" };
        graph.CallersByMethod["First"] = [Caller("Middle")];
        graph.CallersByMethod["Second"] = [Caller("Middle")];
        graph.CallersByMethod["Middle"] = [Caller("Endpoint"), Caller("First")];
        var map = new Dictionary<string, List<ActionGroupInfo>>
        {
            ["Dee"] = [new() { ActionGroup = "Custom.Type.First.Pre", SourceLocation = "a.json" },
                new() { ActionGroup = "Custom.Type.Second.Post", SourceLocation = "a.json" }]
        };
        var summaries = MasterDataActionGroupResolver.Summarize(map, graph,
            new Dictionary<string, ServiceEndpointInfo> { ["Endpoint"] = new() { Endpoint = "POST /items", SourceLocation = "controller.cs" } });
        Assert.All(summaries, summary => Assert.Equal(new[] { "POST /items" }, summary.PossibleServiceEndpoints));
        Assert.All(MasterDataActionGroupResolver.Summarize(map), summary => Assert.Empty(summary.PossibleServiceEndpoints));
    }
}
