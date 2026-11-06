using System.CommandLine;
using System.Text.Json;
using Cmf.Cli.Plugin.CodeReview;
using Cmf.Cli.Plugin.CodeReview.Commands;
using Cmf.Cli.Plugin.CodeReview.Enums;
using Cmf.Cli.Plugin.CodeReview.Models;
using Cmf.Cli.Plugin.CodeReview.Rules;
using Xunit;

namespace codereview.tests;

public sealed class DeploymentVersionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "deployment-versions-" + Guid.NewGuid());
    private string Package => Path.Combine(_root, "Cmf.Test.Data");
    private string Manifest => Path.Combine(Package, "cmfpackage.json");

    public DeploymentVersionTests()
    {
        Directory.CreateDirectory(Package);
        WriteManifest("2.61.0");
    }
    private void WriteManifest(string version) => File.WriteAllText(Manifest, $$"""
        { "packageType": "Data", "version": "{{version}}", "contentToPack": [
          { "contentType": "MasterData", "source": "MasterData/$(version)/*" },
          { "contentType": "EntityTypes", "source": "DEEs/ProcessRules/EntityTypes/$(version)/*" },
          { "contentType": "ProcessRulesPre", "source": "DEEs/ProcessRules/$(version)/Before/*" },
          { "contentType": "ProcessRulesPost", "source": "DEEs/ProcessRules/$(version)/After/*" },
          { "contentType": "ProcessRulesPost", "source": "DEEs/ProcessRules/EntityTypesAttributes/$(version)/*" },
          { "contentType": "ProcessRulesPost", "source": "DEEs/ProcessRules/Services/*" }
        ] }
        """);
    private string Write(string relative)
    {
        var full = Path.Combine(Package, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, relative.EndsWith(".json") ? "{}" : "// deployment rule");
        return Path.GetRelativePath(_root, full);
    }
    private List<Finding> Validate(IEnumerable<string> added, IEnumerable<string>? changed = null, CodeReviewSettings? settings = null, CodeReviewBaseline? baseline = null) =>
        DeploymentVersionValidator.GetFindings(_root, added, changed, settings, baseline).ToList();

    [Theory]
    [InlineData("MasterData/{0}/data.json")]
    [InlineData("DEEs/ProcessRules/{0}/Before/Rule.cs")]
    [InlineData("DEEs/ProcessRules/{0}/After/Rule.cs")]
    [InlineData("DEEs/ProcessRules/EntityTypes/{0}/Rule.cs")]
    [InlineData("DEEs/ProcessRules/EntityTypesAttributes/{0}/Rule.cs")]
    public void EachVersionedContentType_ValidatesPackageVersion(string pattern)
    {
        var wrong = Write(string.Format(pattern, "2.60.0"));
        var finding = Assert.Single(Validate([wrong]));
        Assert.Equal("DeploymentVersionMismatch", finding.Rule);
        Assert.Equal(Severity.High, finding.Severity);
        Assert.Equal(1, finding.LineNumber);
        Assert.Contains("2.61.0", finding.Description);
        Assert.Contains("2.60.0", finding.Description);
        Assert.Contains("cmfpackage.json", finding.Description);
        Assert.Contains("2.61.0", finding.SuggestedFix);
        var correct = Write(string.Format(pattern, "2.61.0"));
        Assert.Empty(Validate([correct]));
    }

    [Fact]
    public void HistoricalEditIsNotAnAddedFile_AndServicesAreUnversioned()
    {
        var old = Write("MasterData/2.59.0/data.json");
        Assert.Empty(Validate([], [old]));
        Assert.Empty(Validate([Write("DEEs/ProcessRules/Services/Rule.cs")]));
    }

    [Fact]
    public void RenamedDestinationAndWindowsPathsAreValidated() =>
        Assert.Single(Validate([Write("MasterData/2.60.0/renamed.json").Replace('/', '\\')]));

    [Fact]
    public void VersionComesFromTheCurrentManifest_IncludingAVersionBump()
    {
        var file = Write("MasterData/2.61.0/data.json");
        Assert.Empty(Validate([file]));
        WriteManifest("2.62.0");
        Assert.Contains("2.62.0", Assert.Single(Validate([file])).Description);
    }

    [Fact]
    public void NearestPackageOwnsItsVersion()
    {
        var nested = Path.Combine(Package, "Nested");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "cmfpackage.json"), """{"packageType":"Data","version":"9.0.0","contentToPack":[{"contentType":"MasterData","source":"MasterData/$(version)/*"}]}""");
        Assert.Empty(Validate([Write("Nested/MasterData/9.0.0/data.json")]));
    }

    [Fact]
    public void MissingVersionAndMalformedManifestAreUnresolved()
    {
        var file = Write("MasterData/2.60.0/data.json");
        File.WriteAllText(Manifest, File.ReadAllText(Manifest).Replace("\"version\": \"2.61.0\",", ""));
        Assert.Equal("DeploymentVersionUnresolved", Assert.Single(Validate([file])).Rule);
        File.WriteAllText(Manifest, "{");
        Assert.Equal("DeploymentVersionUnresolved", Assert.Single(Validate([file])).Rule);
    }

    [Fact]
    public void ChangedFileFilterDeletedFilesAndFilesOutsideRootAreRespected()
    {
        var file = Write("MasterData/2.60.0/data.json");
        Assert.Empty(Validate([file], []));
        Assert.Empty(Validate([Path.Combine(_root, "..", "outside.json")]));
        File.Delete(Path.Combine(_root, file));
        Assert.Empty(Validate([file]));
    }

    [Fact]
    public void SettingsAndBaselineApplyToMetadataFindings()
    {
        var file = Write("MasterData/2.60.0/data.json");
        var settings = new CodeReviewSettings { Rules = new() { ["DeploymentVersionMismatch"] = new RuleSetting { Enabled = false } } };
        Assert.Empty(Validate([file], settings: settings));
        settings.Rules["DeploymentVersionMismatch"] = new() { Severity = "Critical" };
        Assert.Equal(Severity.Critical, Assert.Single(Validate([file], settings: settings)).Severity);
        CodeReviewBaseline.Write(_root, Validate([file]));
        Assert.Empty(Validate([file], baseline: CodeReviewBaseline.Load(_root)));
    }

    [Fact]
    public async Task PipelineStyleInvocation_ReportsNewMasterDataJsonAndFailsTheGate()
    {
        var file = Write("MasterData/2.60.0/data.json");
        var list = Path.Combine(_root, "changed.txt");
        var added = Path.Combine(_root, "added.txt");
        var output = Path.Combine(_root, "findings.json");
        File.WriteAllText(list, file + Environment.NewLine);
        File.WriteAllText(added, file + Environment.NewLine);
        var command = new Command("scan");
        new ScanCommand().Configure(command);
        var code = await command.InvokeAsync(["--repository-root", _root, "--changed-files-list", list,
            "--added-files-list", added, "--output", output, "--fail-on", "High"]);
        Assert.Equal(1, code);
        var findings = JsonSerializer.Deserialize<List<Finding>>(File.ReadAllText(output))!;
        Assert.Equal("DeploymentVersionMismatch", Assert.Single(findings).Rule);
        Assert.Equal(file, findings[0].RelativePath);
        Assert.Contains("DeploymentVersionMismatch", ReportCommand.BuildContent(output, ReportMode.Detailed, ReportFormat.Html, "Review", "Summary"));
        using var sarif = JsonDocument.Parse(ReportCommand.BuildContent(output, ReportMode.Detailed, ReportFormat.Sarif, "Review", "Summary"));
        Assert.Equal("DeploymentVersionMismatch", sarif.RootElement.GetProperty("runs")[0].GetProperty("results")[0].GetProperty("ruleId").GetString());
    }

    [Fact]
    public void WithoutAnAddedFileList_ScannerDoesNotRevalidateHistoricalVersions()
    {
        Write("MasterData/2.60.0/data.json");
        Assert.Empty(CmFrameworkScanner.GetFindings(_root, entityCatalog: EntityCatalog.Empty));
    }

    [Fact]
    public void OverlappingRoots_ReportProcessRuleAndAddedFileVersionFindingsOnce()
    {
        var file = Write("DEEs/ProcessRules/EntityTypesAttributes/2.60.0/Rule.cs");
        File.WriteAllText(Path.Combine(_root, file), ProcessRuleValidationTests.Source(
            "IAction target = provider.GetService<IAction>(); target.Delete();",
            ns: "EntityTypesAttributes._2_60_0"));
        var roots = CodeReviewPaths.ResolveRoots(_root, ReviewScope.DEEs);

        var findings = CmFrameworkScanner.GetFindings(_root, roots: roots.Concat(roots),
            changedFiles: [file], addedFiles: [file], entityCatalog: TestEntities.Catalog);

        Assert.Single(findings, finding => finding.Rule == "ProcessRuleUnguardedMutation");
        Assert.Single(findings, finding => finding.Rule == "DeploymentVersionMismatch");
        Assert.All(findings, finding => Assert.Equal(file, finding.RelativePath));
    }

    [Theory]
    [InlineData("MasterData/$(version)/**/*.json", "MasterData/2.60.0/data.json")]
    [InlineData("MasterData/$(version)/**/*.json", "MasterData/2.60.0/sub/data.json")]
    [InlineData("MasterData/2.61.0/*", "MasterData/2.60.0/data.json")]
    public void RecursiveAndLiteralVersionSourcesStillDetectWrongFolders(string source, string file)
    {
        File.WriteAllText(Manifest, System.Text.Json.JsonSerializer.Serialize(new
        {
            packageType = "Data", version = "2.61.0",
            contentToPack = new[] { new { contentType = "MasterData", source } }
        }));
        Assert.Equal("DeploymentVersionMismatch", Assert.Single(Validate([Write(file)])).Rule);
    }

    public void Dispose() => Directory.Delete(_root, true);
}
