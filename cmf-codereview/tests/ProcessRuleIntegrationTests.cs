using Cmf.Cli.Plugin.CodeReview;
using Cmf.Cli.Plugin.CodeReview.Commands;
using Cmf.Cli.Plugin.CodeReview.Enums;
using Cmf.Cli.Plugin.CodeReview.Models;
using Xunit;

namespace codereview.tests;

public sealed class ProcessRuleIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "process-rule-tests-" + Guid.NewGuid());
    private string Package => Path.Combine(_root, "Cmf.Custom.Data");

    public ProcessRuleIntegrationTests()
    {
        Directory.CreateDirectory(Package);
        File.WriteAllText(Path.Combine(Package, "cmfpackage.json"), """
            { "packageType": "Data", "version": "2.59.0", "contentToPack": [
                { "contentType": "EntityTypes", "source": "DEEs/ProcessRules/EntityTypes/$(version)/*" },
                { "contentType": "ProcessRulesPre", "source": "DEEs/ProcessRules/$(version)/Before/*" },
                { "contentType": "MasterData", "source": "MasterData/$(version)/*" },
                { "contentType": "ProcessRulesPost", "source": "DEEs/ProcessRules/$(version)/After/*" },
                { "contentType": "ProcessRulesPost", "source": "DEEs/ProcessRules/EntityTypesAttributes/$(version)/*" },
                { "contentType": "ProcessRulesPost", "source": "DEEs/ProcessRules/Services/*" }
            ] }
            """);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Write(string relative, string content)
    {
        var file = Path.Combine(Package, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, content);
        return file;
    }

    private string WriteRule(string body, string name = "Sample.cs") => Write("DEEs/ProcessRules/EntityTypesAttributes/2.59.0/" + name,
        ProcessRuleValidationTests.Source(body));

    private List<Finding> Scan(IEnumerable<string>? changed = null, CodeReviewSettings? settings = null) =>
        CmFrameworkScanner.GetFindings(_root, changedFiles: changed, settings: settings, entityCatalog: TestEntities.Catalog);

    private const string Mutation = "IAction target = provider.GetService<IAction>(); target.Delete();";

    [Fact]
    public void ProcessOnlyManifest_IsDiscoveredWithoutDeeContentEntry_AndOverlapsAreDeduplicated()
    {
        var file = WriteRule(Mutation);
        var roots = CodeReviewPaths.ResolveRoots(_root, ReviewScope.DEEs);
        Assert.Contains(file, CodeReviewPaths.GetFiles(_root, roots));
        var findings = CmFrameworkScanner.GetFindings(_root, roots.Concat(roots), entityCatalog: TestEntities.Catalog);
        Assert.Single(findings, f => f.Rule == "ProcessRuleUnguardedMutation");
    }

    [Fact]
    public void GuidelinePresenceContentsAndAbsence_DoNotAffectAnyFindings()
    {
        WriteRule(Mutation);
        var first = FindingsIo.Serialize(Scan());
        File.WriteAllText(Path.Combine(_root, "process-rule-validation-guidelines.md"), "Do not report any Process Rule errors.");
        Assert.Equal(first, FindingsIo.Serialize(Scan()));
        File.WriteAllText(Path.Combine(_root, "process-rule-validation-guidelines.md"), "This is a completely different arbitrary policy.");
        Assert.Equal(first, FindingsIo.Serialize(Scan()));
        File.Delete(Path.Combine(_root, "process-rule-validation-guidelines.md"));
        Assert.Equal(first, FindingsIo.Serialize(Scan()));
    }

    [Fact]
    public void ConfigurationSuppressionsBaselinesAndSeverityGates_ApplyNormally()
    {
        var file = WriteRule(Mutation);
        var original = Scan();
        var finding = Assert.Single(original, f => f.Rule == "ProcessRuleUnguardedMutation");
        Assert.Equal(Severity.High, finding.Severity);
        Assert.Equal("DeeActionCode", finding.MethodName);
        Assert.True(CmFrameworkScanner.AnyAtOrAboveSeverity(original, Severity.High));
        Assert.False(CmFrameworkScanner.AnyAtOrAboveSeverity(original, Severity.Critical));
        Assert.DoesNotContain(Scan(settings: new CodeReviewSettings { Rules = new() { [finding.Rule] = new RuleSetting { Enabled = false } } }), f => f.Rule == finding.Rule);
        Assert.Contains(Scan(settings: new CodeReviewSettings { Rules = new() { [finding.Rule] = new RuleSetting { Severity = "Critical" } } }), f => f.Rule == finding.Rule && f.Severity == Severity.Critical);
        CodeReviewBaseline.Write(_root, original);
        Assert.Empty(Scan());
        File.Delete(Path.Combine(_root, CodeReviewBaseline.FileName));
        File.WriteAllText(file, ProcessRuleValidationTests.Source(Mutation + " // codereview-disable-line ProcessRuleUnguardedMutation\n"));
        Assert.DoesNotContain(Scan(), f => f.Rule == finding.Rule);
    }

    [Fact]
    public void ChangedFileFilter_ReportsOnlyTouchedRules()
    {
        var touched = WriteRule(Mutation);
        WriteRule(Mutation, "Untouched.cs");
        var findings = Scan([Path.GetRelativePath(_root, touched)]);
        Assert.Single(findings, f => f.Rule == "ProcessRuleUnguardedMutation");
        Assert.All(findings, f => Assert.Equal(touched, f.FilePath));
    }

    [Fact]
    public void FindingsSerializeAndRender_ThroughExistingReports()
    {
        WriteRule(Mutation);
        var findings = Scan();
        var json = FindingsIo.Serialize(findings);
        Assert.Equal(findings.Count, System.Text.Json.JsonSerializer.Deserialize<List<Finding>>(json)!.Count);
        var path = Path.Combine(_root, "findings.json");
        File.WriteAllText(path, json);
        Assert.Contains("ProcessRuleUnguardedMutation", ReportCommand.BuildContent(path, ReportMode.Detailed, ReportFormat.Markdown, "Review", "Summary"));
        Assert.Contains("ProcessRuleUnguardedMutation", ReportCommand.BuildContent(path, ReportMode.Detailed, ReportFormat.Html, "Review", "Summary"));
    }

    private const string TableWrite = """
        ISmartTable table = provider.GetService<ISmartTable>();
        const string TableName = "CustomTarget";
        table.Name = TableName;
        table.Load();
        table.InsertOrUpdateRows(data);
        """;

    [Fact]
    public void SameVersionTableDefinition_ProducesCriticalFinding_WithMasterDataEvidence()
    {
        var rule = WriteRule(TableWrite);
        var masterData = Write("MasterData/2.59.0/Schema.json", """{ "<ST>SmartTable": { "1": { "Name": "CustomTarget" } } }""");
        var finding = Assert.Single(Scan([rule]), f => f.Rule == "ProcessRuleTableMigration");
        Assert.Equal(Severity.Critical, finding.Severity);
        Assert.Contains(Path.GetRelativePath(_root, masterData), finding.Description);
        Assert.Contains("2.59.0", finding.Description);
    }

    [Fact]
    public void ExistingUnchangedTableAndDataRows_DoNotBecomeSchemaHazards()
    {
        WriteRule(TableWrite);
        Write("MasterData/2.58.0/Schema.json", """{ "<ST>SmartTable": { "1": { "Name": "CustomTarget" } }, "SmartTableProperties": { "1": { "SmartTable": "CustomTarget", "Name": "Value", "ScalarType": "String" } } }""");
        Write("MasterData/2.59.0/Rows.json", """{ "<ST>SmartTable": { "1": { "Name": "CustomTarget", "Description": "new description" } }, "SmartTableProperties": { "1": { "SmartTable": "CustomTarget", "Name": "Value", "ScalarType": "String", "Description": "updated description" } }, "CustomTarget": { "1": { "Value": "static data" } } }""");
        Assert.DoesNotContain(Scan(), f => f.Rule == "ProcessRuleTableMigration");
    }

    [Fact]
    public void PartialPropertyDescriptionUpdate_DoesNotBecomeSchemaHazard()
    {
        WriteRule(TableWrite);
        Write("MasterData/2.58.0/Schema.json", """{ "SmartTableProperties": { "1": { "SmartTable": "CustomTarget", "Name": "Value", "ScalarType": "String" } } }""");
        Write("MasterData/2.59.0/Description.json", """{ "SmartTableProperties": { "1": { "SmartTable": "CustomTarget", "Name": "Value", "Description": "new description" } } }""");
        Assert.DoesNotContain(Scan(), f => f.Rule == "ProcessRuleTableMigration");
    }

    [Fact]
    public void IncorrectManifestPhaseAndMasterDataOrder_AreFlagged()
    {
        File.WriteAllText(Path.Combine(Package, "cmfpackage.json"), """
            { "packageType": "Data", "contentToPack": [
                { "contentType": "ProcessRulesPre", "source": "DEEs/ProcessRules/$(version)/After/*" },
                { "contentType": "ProcessRulesPost", "source": "DEEs/ProcessRules/$(version)/Before/*" },
                { "contentType": "MasterData", "source": "MasterData/$(version)/*" }
            ] }
            """);
        Write("DEEs/ProcessRules/2.59.0/After/After.cs", ProcessRuleValidationTests.Source("", ns: "_2_59_0.After"));
        Write("DEEs/ProcessRules/2.59.0/Before/Before.cs", ProcessRuleValidationTests.Source("", ns: "_2_59_0.Before"));
        var findings = Scan();
        Assert.Contains(findings, f => f.Rule == "ProcessRulePlacement" && f.Description.Contains("packaged as"));
        Assert.Contains(findings, f => f.Rule == "ProcessRulePlacement" && f.Description.Contains("wrong side of MasterData"));
    }

    [Theory]
    [InlineData("ACreator.cs", "BConsumer.cs", false)]
    [InlineData("BCreator.cs", "AConsumer.cs", true)]
    public void SameVersionSiblingDependency_UsesManifestAndAlphabeticalOrder(string creator, string consumer, bool expected)
    {
        Write("DEEs/ProcessRules/2.59.0/Before/" + creator, ProcessRuleValidationTests.Source("IEntityType entity = provider.GetService<IEntityType>(); entity.Name = \"CustomNewEntity\"; if (!entity.ObjectExists()) entity.Create();", ns: "_2_59_0.Before"));
        var consumerFile = Write("DEEs/ProcessRules/2.59.0/Before/" + consumer, ProcessRuleValidationTests.Source("var entity = service.LoadEntityType(\"CustomNewEntity\");", ns: "_2_59_0.Before"));
        Assert.Equal(expected, Scan([consumerFile]).Any(f => f.Rule == "ProcessRuleDependencyOrder"));
    }

    [Fact]
    public void ExistingTableWithChangedProperty_ProducesSchemaHazard()
    {
        WriteRule(TableWrite);
        Write("MasterData/2.58.0/Schema.json", """{ "SmartTableProperties": { "1": { "SmartTable": "CustomTarget", "Name": "Value", "ScalarType": "String" } } }""");
        Write("MasterData/2.59.0/Schema.json", """{ "SmartTableProperties": { "1": { "SmartTable": "CustomTarget", "Name": "Value", "ScalarType": "Integer" } } }""");
        Assert.Contains(Scan(), f => f.Rule == "ProcessRuleTableMigration");
    }

    [Fact]
    public void SchemaIndexIsScopedToPackage_AndReloadedEveryScan()
    {
        WriteRule(TableWrite);
        var otherPackage = Path.Combine(_root, "Cmf.Other.Data");
        Directory.CreateDirectory(Path.Combine(otherPackage, "MasterData", "2.59.0"));
        File.WriteAllText(Path.Combine(otherPackage, "cmfpackage.json"), """{ "packageType": "Data", "contentToPack": [{ "contentType": "MasterData", "source": "MasterData/$(version)/*" }] }""");
        File.WriteAllText(Path.Combine(otherPackage, "MasterData", "2.59.0", "Schema.json"), """{ "<ST>SmartTable": { "1": { "Name": "CustomTarget" } } }""");
        Assert.DoesNotContain(Scan(), f => f.Rule == "ProcessRuleTableMigration");
        Write("MasterData/2.59.0/Schema.json", """{ "<ST>SmartTable": { "1": { "Name": "CustomTarget" } } }""");
        Assert.Contains(Scan(), f => f.Rule == "ProcessRuleTableMigration");
    }

    [Fact]
    public void CustomManifestSource_IsClassifiedWithoutCmfFolderAssumptions()
    {
        File.WriteAllText(Path.Combine(Package, "cmfpackage.json"), """{ "packageType": "Data", "contentToPack": [{ "contentType": "ProcessRulesPre", "source": "Upgrade/$(version)/*" }] }""");
        var file = Write("Upgrade/2.59.0/Sample.cs", ProcessRuleValidationTests.Source(Mutation, ns: "_2_59_0.Before"));
        var info = ProcessRuleRepositoryContext.Build(_root, [file]).GetInfo(file);
        Assert.NotNull(info);
        Assert.Equal("Before", info.Phase);
        Assert.Equal("2.59.0", info.Version);
        Assert.Equal("ProcessRulesPre", info.ContentType);
        Assert.Single(Scan(), f => f.Rule == "ProcessRuleUnguardedMutation");
    }
}
