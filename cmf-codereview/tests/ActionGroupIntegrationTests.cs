using System.CommandLine;
using System.Text.Json;
using Cmf.Cli.Plugin.CodeReview;
using Cmf.Cli.Plugin.CodeReview.Commands;
using Cmf.Cli.Plugin.CodeReview.Enums;
using Cmf.Cli.Plugin.CodeReview.Models;
using NJsonSchema;
using Xunit;

namespace codereview.tests;

public class ActionGroupIntegrationTests : IDisposable
{
    private readonly ActionGroupFixture _fixture = new();

    public ActionGroupIntegrationTests()
    {
        var root = _fixture.AddPackage();
        _fixture.Write($"{root}/1.0/orders.json", ActionGroupFixture.Orders(
            ("SharedDee", "Custom.Type.First.Pre"), ("SharedDee", "Custom.Type.Second.Post"),
            ("SharedDee", "Custom.Type.Third.Pre"), ("FindingFreeDee", "Custom.Type.First.Pre")));
        _fixture.Write("Cmf.Custom.Business/cmfpackage.json", """{"packageType":"Business"}""");
        _fixture.Write("Cmf.Custom.Business/Cmf.Custom.Orchestration/Methods.cs", """
            public class Methods
            {
                public void First() { this.Second(); }
                public void Second() { this.Third(); }
                public void Third() { }
                public void Costly() { foreach (var item in items) { item.Load(); } }
            }
            """);
        _fixture.Write("Cmf.Custom.Business/Cmf.Custom.Services/ExampleController.cs", """
            public class ExampleController
            {
                [HttpPost, Route("/items")]
                public void Endpoint() { methods.First(); }
            }
            """);
    }

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void ScopedFilteredAndBaselinedScan_PreservesAllWiringIncludingFindingFreeDees()
    {
        var findings = CmFrameworkScanner.GetFindings(_fixture.Root, null, null, null, true, TestEntities.Catalog, out var summaries);
        Assert.Contains(findings, finding => finding.Rule == "LoadInsideLoop");
        Assert.Equal(3, summaries.Count);
        var first = summaries.Single(summary => summary.ActionGroup == "Custom.Type.First.Pre");
        Assert.Equal(2, first.DeeCount);
        Assert.Equal(ActionGroupRiskLevel.Medium, first.RiskLevel);
        Assert.All(summaries, summary => Assert.Equal(new[] { "POST /items" }, summary.PossibleServiceEndpoints));
        var expected = JsonSerializer.Serialize(summaries);
        var changed = CmFrameworkScanner.GetFindings(_fixture.Root, ["Cmf.Custom.Business/Cmf.Custom.Orchestration"],
            Array.Empty<string>(), null, true, TestEntities.Catalog, out var filteredSummaries);
        Assert.Empty(changed);
        Assert.Equal(expected, JsonSerializer.Serialize(filteredSummaries));
        CodeReviewBaseline.Write(_fixture.Root, findings);
        Assert.Empty(CmFrameworkScanner.GetFindings(_fixture.Root, null, null, null, true, TestEntities.Catalog, out var baselineSummaries));
        Assert.Equal(expected, JsonSerializer.Serialize(baselineSummaries));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ScanCommand_OptInContractPreservesFindingsAndExitCode(bool enriched, bool sarif)
    {
        var command = new Command("scan");
        new ScanCommand().Configure(command);
        var path = Path.Combine(_fixture.Root, "findings.json");
        var args = new List<string> { "--repository-root", _fixture.Root, "--output", path, "--fail-on", "High" };
        if (enriched) args.Add("--include-action-group-summary");
        if (sarif) args.AddRange(["--report-format", "Sarif"]);
        Assert.Empty(command.Parse(args.ToArray()).Errors);
        Assert.Equal(1, await command.InvokeAsync(args.ToArray(), null));
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (sarif)
        {
            var run = doc.RootElement.GetProperty("runs")[0];
            Assert.Equal(3, run.GetProperty("properties").GetProperty("actionGroupSummaries").GetArrayLength());
            Assert.Single(run.GetProperty("results").EnumerateArray());
        }
        else
        {
            var findings = enriched ? doc.RootElement.GetProperty("Findings") : doc.RootElement;
            Assert.Single(findings.EnumerateArray());
            if (enriched) Assert.Equal(3, doc.RootElement.GetProperty("ActionGroupSummaries").GetArrayLength());
        }
    }

    [Fact]
    public void DisabledSuppressedAndRegradedFindings_DoNotChangeWiringRisk()
    {
        var settings = new CodeReviewSettings { Rules = new() { ["LoadInsideLoop"] = new RuleSetting { Enabled = false } } };
        Assert.Empty(CmFrameworkScanner.GetFindings(_fixture.Root, null, null, settings, true, TestEntities.Catalog, out var disabledSummaries));
        settings = new CodeReviewSettings { Rules = new() { ["LoadInsideLoop"] = new RuleSetting { Severity = "Critical" } } };
        var regraded = CmFrameworkScanner.GetFindings(_fixture.Root, null, null, settings, true, TestEntities.Catalog, out var regradedSummaries);
        Assert.Equal(Severity.Critical, Assert.Single(regraded).Severity);
        var source = Path.Combine(_fixture.Root, "Cmf.Custom.Business", "Cmf.Custom.Orchestration", "Methods.cs");
        File.WriteAllText(source, File.ReadAllText(source).Replace("item.Load(); } }", "item.Load(); } } // codereview-disable-line LoadInsideLoop"));
        Assert.Empty(CmFrameworkScanner.GetFindings(_fixture.Root, null, null, null, true, TestEntities.Catalog, out var suppressedSummaries));
        Assert.Equal(JsonSerializer.Serialize(disabledSummaries), JsonSerializer.Serialize(regradedSummaries));
        Assert.Equal(JsonSerializer.Serialize(disabledSummaries), JsonSerializer.Serialize(suppressedSummaries));
        Assert.Equal(ActionGroupRiskLevel.Medium, disabledSummaries.Single(summary => summary.DeeCount == 2).RiskLevel);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReviewCommand_ReportUsesTheScanSummaryWithEitherJsonContract(bool enriched)
    {
        var command = new Command("review");
        new ReviewCommand().Configure(command);
        var findingsPath = Path.Combine(_fixture.Root, "findings.json");
        var reportPath = Path.Combine(_fixture.Root, "review.md");
        var args = new List<string> { "--repository-root", _fixture.Root, "--findings-path", findingsPath, "--report-path", reportPath };
        if (enriched) args.Add("--include-action-group-summary");
        Assert.Empty(command.Parse(args.ToArray()).Errors);
        Assert.Equal(0, await command.InvokeAsync(args.ToArray(), null));
        using var doc = JsonDocument.Parse(File.ReadAllText(findingsPath));
        Assert.Equal(enriched ? JsonValueKind.Object : JsonValueKind.Array, doc.RootElement.ValueKind);
        var report = File.ReadAllText(reportPath);
        Assert.Contains("Custom.Type.First.Pre | 2 | Medium", report);
        Assert.Contains("FindingFreeDee", report);
        Assert.Contains("POST /items", report);
    }

    [Theory]
    [InlineData(ReportMode.Standard, ReportFormat.Markdown)]
    [InlineData(ReportMode.Detailed, ReportFormat.Markdown)]
    [InlineData(ReportMode.Standard, ReportFormat.Html)]
    [InlineData(ReportMode.Detailed, ReportFormat.Html)]
    [InlineData(ReportMode.Standard, ReportFormat.Sarif)]
    [InlineData(ReportMode.Detailed, ReportFormat.Sarif)]
    public async Task EnrichedFindings_RoundTripEveryReportWithoutTheRepository(ReportMode mode, ReportFormat format)
    {
        var findings = CmFrameworkScanner.GetFindings(_fixture.Root, null, null, null, true, TestEntities.Catalog, out var summaries);
        var path = _fixture.Write("findings.json", FindingsIo.Serialize(findings, summaries));
        // Remove MasterData to prove reports consume the recorded summary, not a new scan.
        Directory.Delete(Path.Combine(_fixture.Root, "Cmf.Sample.Data", "Wiring"), recursive: true);
        var report = ReportCommand.BuildContent(path, mode, format, "Review", "Summary", _fixture.Root);
        Assert.Contains("Custom.Type.First.Pre", report);
        Assert.Contains("FindingFreeDee", report);
        Assert.Contains("POST /items", report);
        Assert.Contains("LoadInsideLoop", report);
        if (format == ReportFormat.Sarif)
        {
            var schema = await JsonSchema.FromJsonAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sarif-schema-2.1.0.json")));
            Assert.Empty(schema.Validate(report));
            using var doc = JsonDocument.Parse(report);
            Assert.Single(doc.RootElement.GetProperty("runs")[0].GetProperty("results").EnumerateArray());
        }
    }

    [Fact]
    public void LegacyReportWithRepository_ResolvesCountsWithoutInventingEndpointEvidence()
    {
        var path = _fixture.Write("legacy.json", "[]");
        var report = ReportCommand.BuildContent(path, ReportMode.Standard, ReportFormat.Markdown, "Review", "Summary", _fixture.Root);
        Assert.Contains("Custom.Type.First.Pre | 2 | Medium", report);
        Assert.DoesNotContain("POST /items", report);
        Assert.DoesNotContain("Action Group Summary", ReportCommand.BuildContent(path, ReportMode.Standard, ReportFormat.Markdown, "Review", "Summary"));
    }

    [Fact]
    public void EmptyRecordedSummary_IsAuthoritativeEvenWhenRepositoryHasWiring()
    {
        var path = _fixture.Write("empty.json", FindingsIo.Serialize([], []));
        var report = ReportCommand.BuildContent(path, ReportMode.Standard, ReportFormat.Markdown, "Review", "Summary", _fixture.Root);
        Assert.Contains("No MasterData DEE wiring found.", report);
        Assert.DoesNotContain("Custom.Type.First.Pre", report);
    }

    [Fact]
    public void LegacyAndEnrichedIo_KeepFindingsAndExistingNumericSeverityReadable()
    {
        var findings = CmFrameworkScanner.GetFindings(_fixture.Root, null, null, null, true, TestEntities.Catalog, out var summaries);
        var legacy = FindingsIo.Serialize(findings);
        Assert.Equal(JsonSerializer.Serialize(findings), JsonSerializer.Serialize(JsonSerializer.Deserialize<List<Finding>>(legacy)));
        Assert.Null(FindingsIo.Deserialize(legacy).ActionGroupSummaries);
        var enriched = FindingsIo.Deserialize(FindingsIo.Serialize(findings, summaries));
        Assert.Equal(legacy, FindingsIo.Serialize(enriched.Findings));
        Assert.Equal(JsonSerializer.Serialize(summaries), JsonSerializer.Serialize(enriched.ActionGroupSummaries));
        var numeric = legacy.Replace("\"Severity\": \"High\"", "\"Severity\": 1");
        Assert.Equal(Severity.High, Assert.Single(FindingsIo.Deserialize(numeric).Findings).Severity);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"Findings\":null}")]
    [InlineData("{\"Findings\":{}}")]
    [InlineData("{\"Findings\":[],\"ActionGroupSummaries\":42}")]
    public void InvalidFindings_DoNotSilentlyBecomeACleanReport(string json) =>
        Assert.Throws<JsonException>(() => FindingsIo.Deserialize(json));

    [Fact]
    public void SummaryText_IsEscapedInHtmlAndMarkdown()
    {
        List<ActionGroupSummary> summaries = [new() { ActionGroup = "<script>|Group", DeeCount = 1,
            RiskLevel = ActionGroupRiskLevel.Low, Dees = ["<img>|Dee\nnext"] }];
        var html = CmHtmlReportGenerator.BuildStandardReport([], "Review", "Summary", summaries);
        Assert.DoesNotContain("<script>|Group", html);
        Assert.Contains("&lt;script&gt;|Group", html);
        var markdown = CmReportGenerator.BuildDetailedReport([], "Review", "Summary", summaries);
        Assert.Contains("&lt;script&gt;&#124;Group", markdown);
        Assert.DoesNotContain("Dee\nnext", markdown);
    }
}
