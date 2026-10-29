using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cmf.Cli.Plugin.CodeReview;
using Cmf.Cli.Plugin.CodeReview.Commands;
using Cmf.Cli.Plugin.CodeReview.Enums;
using Cmf.Cli.Plugin.CodeReview.Models;
using NJsonSchema;
using Xunit;

namespace codereview.tests;

/// <summary>
/// Work-item acceptance checks for --report-format Sarif. Run from cmf-codereview with:
/// dotnet test tests/CodeReview.Tests.csproj --filter FullyQualifiedName~SarifAcceptanceTests
/// These use the real generators and ReportCommand; no installed CLI or external analyzer is needed.
/// </summary>
public class SarifAcceptanceTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "codereview-sarif-" + Guid.NewGuid());

    public SarifAcceptanceTests() => Directory.CreateDirectory(_tempRoot);

    public void Dispose() => Directory.Delete(_tempRoot, recursive: true);

    private static Finding MakeFinding(string rule, Severity severity, int line = 3, string path = "src/Example.cs") => new()
    {
        FilePath = Path.Combine(Path.GetTempPath(), path.Replace('/', Path.DirectorySeparatorChar)),
        RelativePath = path,
        LineNumber = line,
        Severity = severity,
        Rule = rule,
        Description = $"Finding for {rule}",
        Snippet = "example();",
        SuggestedFix = $"Fix {rule}"
    };

    private string WriteFindings(List<Finding> findings)
    {
        var path = Path.Combine(_tempRoot, "findings.json");
        File.WriteAllText(path, FindingsIo.Serialize(findings));
        return path;
    }

    [Fact]
    public void SarifReport_HasVersionToolRulesResultsAndSourceLocation()
    {
        // Check that a finding appears in a SARIF document with its rule, message, and file location.
        var json = CmSarifReportGenerator.BuildStandardReport(
            new[] { MakeFinding("RuleA", Severity.High) }, "Title", "Summary");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal("2.1.0", root.GetProperty("version").GetString());
        Assert.Equal(JsonValueKind.Array, root.GetProperty("runs").ValueKind);
        var run = root.GetProperty("runs")[0];
        var driver = run.GetProperty("tool").GetProperty("driver");
        Assert.Equal("cmf-codereview", driver.GetProperty("name").GetString());
        Assert.Equal("RuleA", driver.GetProperty("rules")[0].GetProperty("id").GetString());
        var result = run.GetProperty("results")[0];
        Assert.Equal("RuleA", result.GetProperty("ruleId").GetString());
        Assert.Contains("Finding for RuleA", result.GetProperty("message").GetProperty("text").GetString());
        var location = result.GetProperty("locations")[0].GetProperty("physicalLocation");
        Assert.Equal("src/Example.cs", location.GetProperty("artifactLocation").GetProperty("uri").GetString());
        Assert.Equal(3, location.GetProperty("region").GetProperty("startLine").GetInt32());
    }

    [Fact]
    public async Task GeneratedReportsMatchTheOfficialSarifSchema()
    {
        // Check both report modes, an empty report, and an unknown line against the official SARIF schema.
        var schemaPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "sarif-schema-2.1.0.json");
        var schemaText = await File.ReadAllTextAsync(schemaPath);
        var schema = await JsonSchema.FromJsonAsync(schemaText);
        using var schemaDocument = JsonDocument.Parse(schemaText);
        var schemaUri = schemaDocument.RootElement.GetProperty("id").GetString();
        var findingsPath = WriteFindings([
            MakeFinding("KnownLine", Severity.High),
            MakeFinding("UnknownLine", Severity.Medium, line: 0)
        ]);

        foreach (var mode in new[] { ReportMode.Standard, ReportMode.Detailed })
        {
            var report = ReportCommand.BuildContent(findingsPath, mode, ReportFormat.Sarif, "Title", "Summary");
            using var reportDocument = JsonDocument.Parse(report);
            Assert.Equal(schemaUri, reportDocument.RootElement.GetProperty("$schema").GetString());
            Assert.Empty(schema.Validate(report));
        }

        var emptyReport = CmSarifReportGenerator.BuildStandardReport([], "Title", "Summary");
        Assert.Empty(schema.Validate(emptyReport));

        // Make an invalid line on purpose so this test also proves that schema validation is working.
        var invalidReport = JsonNode.Parse(ReportCommand.BuildContent(findingsPath, ReportMode.Standard,
            ReportFormat.Sarif, "Title", "Summary"))!;
        invalidReport["runs"]![0]!["results"]![0]!["locations"]![0]!["physicalLocation"]!["region"]!["startLine"] = 0;
        Assert.NotEmpty(schema.Validate(invalidReport.ToJsonString()));
    }

    [Fact]
    public void EmptyFindings_ProduceAnEmptySarifRun()
    {
        // A clean scan should still produce a SARIF document, with no rules or results.
        using var document = JsonDocument.Parse(CmSarifReportGenerator.BuildStandardReport([], "Title", "Summary"));
        var run = document.RootElement.GetProperty("runs")[0];

        Assert.Empty(run.GetProperty("tool").GetProperty("driver").GetProperty("rules").EnumerateArray());
        Assert.Empty(run.GetProperty("results").EnumerateArray());
    }

    [Fact]
    public void RulesAreDistinctAndSorted_WhileResultsRetainTheirFindingRule()
    {
        // List each rule once in alphabetical order, while keeping every finding's own rule ID.
        var findings = new[]
        {
            MakeFinding("Zulu", Severity.High),
            MakeFinding("Alpha", Severity.Medium),
            MakeFinding("Zulu", Severity.Critical)
        };
        using var document = JsonDocument.Parse(CmSarifReportGenerator.BuildStandardReport(findings, "Title", "Summary"));
        var run = document.RootElement.GetProperty("runs")[0];

        Assert.Equal(new[] { "Alpha", "Zulu" }, run.GetProperty("tool").GetProperty("driver").GetProperty("rules")
            .EnumerateArray().Select(rule => rule.GetProperty("id").GetString()));
        Assert.Equal(new[] { "Zulu", "Alpha", "Zulu" }, run.GetProperty("results")
            .EnumerateArray().Select(result => result.GetProperty("ruleId").GetString()));
    }

    [Fact]
    public void SeverityMapsToSarifLevels()
    {
        // Critical and High become errors, Medium becomes a warning, and other values become notes.
        var findings = new[]
        {
            MakeFinding("Critical", Severity.Critical),
            MakeFinding("High", Severity.High),
            MakeFinding("Medium", Severity.Medium),
            MakeFinding("Other", (Severity)999)
        };
        using var document = JsonDocument.Parse(CmSarifReportGenerator.BuildStandardReport(findings, "Title", "Summary"));

        Assert.Equal(new[] { "error", "error", "warning", "note" }, document.RootElement.GetProperty("runs")[0]
            .GetProperty("results").EnumerateArray().Select(result => result.GetProperty("level").GetString()));
    }

    [Fact]
    public void StandardAndDetailedModesProduceTheSameSarif()
    {
        // The two report modes should produce the same SARIF because SARIF has no grouping modes.
        var findingsPath = WriteFindings([MakeFinding("RuleA", Severity.High)]);

        var standard = ReportCommand.BuildContent(findingsPath, ReportMode.Standard, ReportFormat.Sarif, "Title", "Summary");
        var detailed = ReportCommand.BuildContent(findingsPath, ReportMode.Detailed, ReportFormat.Sarif, "Title", "Summary");

        Assert.Equal(standard, detailed);
    }

    [Fact]
    public void DefaultSarifReportPathUsesSarifExtension()
    {
        // When no filename is supplied, the saved report should end in .sarif and contain SARIF JSON.
        var findingsPath = WriteFindings([MakeFinding("RuleA", Severity.High)]);

        var reportPath = ReportCommand.Build(findingsPath, _tempRoot, null, ReportMode.Standard, ReportFormat.Sarif, "Title", "Summary");

        Assert.Equal(".sarif", Path.GetExtension(reportPath));
        Assert.True(File.Exists(reportPath));
        using var document = JsonDocument.Parse(File.ReadAllText(reportPath));
        Assert.Equal(JsonValueKind.Array, document.RootElement.GetProperty("runs").ValueKind);
    }

    [Fact]
    public void MarkdownAndHtmlStillRender()
    {
        // Adding SARIF should not stop the existing Markdown and HTML reports from rendering.
        var findingsPath = WriteFindings([MakeFinding("RuleA", Severity.High)]);

        var markdown = ReportCommand.BuildContent(findingsPath, ReportMode.Standard, ReportFormat.Markdown, "Title", "Summary");
        var html = ReportCommand.BuildContent(findingsPath, ReportMode.Standard, ReportFormat.Html, "Title", "Summary");

        Assert.StartsWith("## Title", markdown);
        Assert.Contains("RuleA", markdown);
        Assert.StartsWith("<!doctype html>", html);
        Assert.Contains("RuleA", html);
    }

    [Fact]
    public void ReviewCommandWritesSarifFromAMinimalRepository()
    {
        // Run review on a tiny repository to check that it writes a SARIF file end to end.
        var repositoryRoot = Path.Combine(_tempRoot, "repository");
        var businessRoot = Path.Combine(repositoryRoot, "Cmf.Custom.Business");
        var orchestrationRoot = Path.Combine(businessRoot, "Cmf.Custom.Orchestration");
        Directory.CreateDirectory(orchestrationRoot);
        File.WriteAllText(Path.Combine(businessRoot, "cmfpackage.json"), """{ "packageType": "Business" }""");
        File.WriteAllText(Path.Combine(orchestrationRoot, "Example.cs"), "public class Example { public void Execute() { } }");
        var findingsPath = Path.Combine(_tempRoot, "review-findings.json");
        var reportPath = Path.Combine(_tempRoot, "review.sarif");

        new ReviewCommand().Execute(repositoryRoot, ReviewScope.Orchestration, [], findingsPath, reportPath,
            ReportMode.Standard, ReportFormat.Sarif, "Title", "Summary");

        Assert.True(File.Exists(findingsPath));
        Assert.True(File.Exists(reportPath));
        using var document = JsonDocument.Parse(File.ReadAllText(reportPath));
        Assert.Equal("cmf-codereview", document.RootElement.GetProperty("runs")[0]
            .GetProperty("tool").GetProperty("driver").GetProperty("name").GetString());
    }

    [Fact]
    public void ScanAcceptsSarifReportFormatOption()
    {
        // Scan should recognize --report-format Sarif instead of rejecting the option.
        var command = new Command("scan");
        new ScanCommand().Configure(command);

        var parsed = command.Parse($"--repository-root {_tempRoot} --report-format Sarif");

        Assert.True(parsed.Errors.Count == 0, string.Join("; ", parsed.Errors.Select(error => error.Message)));
    }

    [Theory]
    [InlineData("Sariff")]
    [InlineData("Html")]
    public void ScanRejectsUnsupportedReportFormat(string format)
    {
        // A misspelled or unsupported format must fail before scan can silently write JSON.
        var command = new Command("scan");
        new ScanCommand().Configure(command);

        var parsed = command.Parse(new[] { "--repository-root", _tempRoot, "--report-format", format });

        Assert.Contains(parsed.Errors, error => error.Message.Contains("--report-format", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("Json", false)]
    [InlineData("json", true)]
    [InlineData("Sarif", false)]
    [InlineData("sarif", true)]
    [InlineData("SARIF", false)]
    public async Task ScanWritesSelectedFormatAndPreservesFailureThreshold(string? format, bool failOnHigh)
    {
        // Exercise format selection and exit codes through the configured command, including default JSON.
        var repositoryRoot = Path.Combine(_tempRoot, "scan-repository");
        var businessRoot = Path.Combine(repositoryRoot, "Cmf.Custom.Business");
        var orchestrationRoot = Path.Combine(businessRoot, "Cmf.Custom.Orchestration");
        Directory.CreateDirectory(orchestrationRoot);
        File.WriteAllText(Path.Combine(businessRoot, "cmfpackage.json"), """{ "packageType": "Business" }""");
        File.WriteAllText(Path.Combine(orchestrationRoot, "Example.cs"), """
            public class Example
            {
                public void Execute()
                {
                    for (int i = 0; i < 10; i++)
                    {
                        entity.Load();
                    }
                }
            }
            """);
        var isSarif = format?.Equals("sarif", StringComparison.OrdinalIgnoreCase) == true;
        var reportPath = Path.Combine(_tempRoot, isSarif ? "scan.sarif" : "scan.json");
        var command = new Command("scan");
        new ScanCommand().Configure(command);

        var args = new List<string>
        {
            "--repository-root", repositoryRoot,
            "--scope", "Orchestration",
            "--output", reportPath
        };
        if (format is not null) args.AddRange(["--report-format", format]);
        if (failOnHigh) args.AddRange(["--fail-on", "High"]);
        var parsed = command.Parse(args.ToArray());
        Assert.Empty(parsed.Errors);
        Assert.Equal(failOnHigh ? 1 : 0, await command.InvokeAsync(args.ToArray(), null));

        Assert.True(File.Exists(reportPath));
        using var document = JsonDocument.Parse(File.ReadAllText(reportPath));
        var results = isSarif ? document.RootElement.GetProperty("runs")[0].GetProperty("results") : document.RootElement;
        Assert.Contains(results.EnumerateArray(), result => result.GetProperty(isSarif ? "ruleId" : "Rule").GetString() == "LoadInsideLoop");
    }

    [Fact]
    public void UnknownLineDoesNotProduceAnInvalidSarifStartLine()
    {
        // An unknown line still reports the file, but it must not claim a source region.
        using var document = JsonDocument.Parse(CmSarifReportGenerator.BuildStandardReport(
            new[] { MakeFinding("RuleA", Severity.High, line: 0) }, "Title", "Summary"));
        var result = document.RootElement.GetProperty("runs")[0].GetProperty("results")[0];
        var location = Assert.Single(result.GetProperty("locations").EnumerateArray());
        var physical = location.GetProperty("physicalLocation");

        Assert.Equal("src/Example.cs", physical.GetProperty("artifactLocation").GetProperty("uri").GetString());
        Assert.False(physical.TryGetProperty("region", out _));
    }

    [Fact]
    public void PathWithSpacesIsEncodedAsAValidUriReference()
    {
        // A file path with spaces must be encoded as a valid URI without losing the original path.
        using var document = JsonDocument.Parse(CmSarifReportGenerator.BuildStandardReport(
            new[] { MakeFinding("RuleA", Severity.High, path: "src/My File.cs") }, "Title", "Summary"));
        var uri = document.RootElement.GetProperty("runs")[0].GetProperty("results")[0]
            .GetProperty("locations")[0].GetProperty("physicalLocation")
            .GetProperty("artifactLocation").GetProperty("uri").GetString();

        Assert.NotNull(uri);
        Assert.True(Uri.IsWellFormedUriString(uri, UriKind.RelativeOrAbsolute), $"Invalid SARIF URI reference: {uri}");
        Assert.Equal("src/My%20File.cs", uri);
        Assert.Equal("src/My File.cs", Uri.UnescapeDataString(uri));
    }
}
