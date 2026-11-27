using System.Text.Json;
using Cmf.Cli.Plugin.CodeReview;
using Cmf.Cli.Plugin.CodeReview.Models;
using Xunit;

namespace codereview.tests;

public class TraceSpanTests
{
    [Fact]
    public void SpanNamesRetainExactGroupAndDistinctSuffixAcrossSerialization()
    {
        var summaries = MasterDataActionGroupResolver.Summarize(new Dictionary<string, List<ActionGroupInfo>>
        {
            ["Dee"] = [new() { ActionGroup = "Platform.Material.TrackOut.Pre", SourceLocation = "a.json" },
                new() { ActionGroup = "Platform.Material.TrackOut.Post", SourceLocation = "a.json" }]
        });
        Assert.All(summaries, summary => Assert.Equal("DEE::" + summary.ActionGroup, summary.DeeSpanName));
        Assert.Equal(2, summaries.Select(s => s.DeeSpanName).Distinct().Count());
        var json = FindingsIo.Serialize([], summaries);
        var roundtrip = FindingsIo.Deserialize(json).ActionGroupSummaries!;
        Assert.Equal(summaries.Select(s => s.DeeSpanName), roundtrip.Select(s => s.DeeSpanName));
        using var sarif = JsonDocument.Parse(CmSarifReportGenerator.BuildStandardReport([], "title", "summary", summaries));
        Assert.Equal(2, sarif.RootElement.GetProperty("runs")[0].GetProperty("properties").GetProperty("actionGroupSummaries").EnumerateArray()
            .Select(s => s.GetProperty("DeeSpanName").GetString()).Distinct().Count());
    }
}
