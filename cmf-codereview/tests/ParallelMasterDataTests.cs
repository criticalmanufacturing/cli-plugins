using System.Text.Json;
using Cmf.Cli.Plugin.CodeReview;
using Xunit;

namespace codereview.tests;

public class ParallelMasterDataTests
{
    [Fact]
    public void ParallelParsingMatchesSequentialAcrossRootsVersionsAndRepeatedRuns()
    {
        using var fixture = new ActionGroupFixture();
        var roots = new[] { fixture.AddPackage("A.Data"), fixture.AddPackage("Z.Data") };
        foreach (var i in Enumerable.Range(0, 80).OrderByDescending(i => i))
            fixture.Write($"{roots[i % 2]}/2.{i}/orders.json", ActionGroupFixture.Orders(("Shared", "A.Pre"), ($"Dee{i}", "B.Post")));
        var sequential = MasterDataActionGroupResolver.GetActionGroupMap(fixture.Root, roots, parallelParsing: false);
        var expected = JsonSerializer.Serialize(sequential);
        Assert.EndsWith("2.79/orders.json", sequential["Shared"][0].SourceLocation);
        for (var i = 0; i < 6; i++)
        {
            var parallel = MasterDataActionGroupResolver.GetActionGroupMap(fixture.Root, i % 2 == 0 ? roots : roots.Reverse());
            Assert.Equal(expected, JsonSerializer.Serialize(parallel));
            Assert.Equal(FindingsIo.Serialize([], MasterDataActionGroupResolver.Summarize(sequential)),
                FindingsIo.Serialize([], MasterDataActionGroupResolver.Summarize(parallel)));
        }
    }
}
