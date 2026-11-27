using System.Text.Json;
using Cmf.CLI.Utilities;
using Cmf.Cli.Plugin.CodeReview;
using Cmf.Cli.Plugin.CodeReview.Commands;
using Cmf.Cli.Plugin.CodeReview.Enums;
using Cmf.Cli.Plugin.CodeReview.Models;
using Xunit;

namespace codereview.tests;

public class NestedConfigurationTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"outer\":\" \"}")]
    [InlineData("null")]
    [InlineData("{\"outer\":\"A\",\"nested\":null}")]
    [InlineData("{\"outer\":\"A\",\"nested\":[\"\"]}")]
    public void InvalidEntriesFailAtLoad(string entry)
    {
        using var fixture = new ActionGroupFixture();
        fixture.Write(".codereview.json", "{\"nestedActionGroups\":[" + entry + "]}");
        Assert.Throws<CliException>(() => CodeReviewSettings.Load(fixture.Root));
    }

    [Fact]
    public void ScanAndFallbackUseOverrides_RecordedSummariesRemainAuthoritative()
    {
        using var fixture = new ActionGroupFixture();
        var root = fixture.AddPackage();
        fixture.Write($"{root}/1.0/orders.json", ActionGroupFixture.Orders(("Dee", "Platform.Module.ComplexTrackOutMaterials.Pre")));
        fixture.Write(".codereview.json", """
            { "nestedActionGroups": [
              { "outer":"ComplexTrackOutMaterials", "nested":["CustomNext"] },
              { "outer":"CustomNext", "nested":["ComplexTrackOutMaterials"] },
              { "outer":"TrackOutMaterials", "nested":[] }
            ] }
            """);
        _ = CmFrameworkScanner.GetFindings(fixture.Root, null, null, null, true, TestEntities.Catalog, out var summaries);
        Assert.Equal(new[] { "CustomNext" }, Assert.Single(summaries).TriggeredActionGroups);
        var settings = CodeReviewSettings.Load(fixture.Root)!;
        Assert.Empty(NestedActionGroupCatalog.BuildEdgeMap(NestedActionGroupCatalog.LoadDefaults(), settings.NestedActionGroups)["TrackOutMaterials"]);
        var legacy = fixture.Write("findings.json", "[]");
        var fallback = ReportCommand.BuildContent(legacy, ReportMode.Standard, ReportFormat.Markdown, "title", "summary", fixture.Root);
        Assert.Contains("CustomNext", fallback);
        fixture.Write("recorded.json", JsonSerializer.Serialize(new { Findings = Array.Empty<Finding>(), ActionGroupSummaries = Array.Empty<ActionGroupSummary>() }));
        var recorded = ReportCommand.BuildContent(Path.Combine(fixture.Root, "recorded.json"), ReportMode.Standard, ReportFormat.Markdown, "title", "summary", fixture.Root);
        Assert.DoesNotContain("CustomNext", recorded);
    }
}
