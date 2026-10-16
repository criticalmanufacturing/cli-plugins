using Cmf.Cli.Plugin.CodeReview;
using Cmf.Cli.Plugin.CodeReview.Enums;
using Cmf.CLI.Utilities;
using Xunit;

namespace codereview.tests;

/// <summary>Per-project configuration: .codereview.json (rule settings + custom rules), inline suppressions, and the findings baseline.</summary>
public class CodeReviewSettingsTests : IDisposable
{
    private readonly string _tempRoot;

    public CodeReviewSettingsTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "codereview-settings-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private void WriteOrchestrationFile(string fileName, string content)
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
        File.WriteAllText(Path.Combine(dir, fileName), content);
    }

    private void WriteSettings(string json) =>
        File.WriteAllText(Path.Combine(_tempRoot, CodeReviewSettings.FileName), json);

    private void WriteLoadInsideLoopFile() =>
        WriteOrchestrationFile("Foo.cs", """
            public class Foo
            {
                public void DoWork()
                {
                    foreach (var item in items)
                    {
                        entity.Load();
                    }
                }
            }
            """);

    [Fact]
    public void DisabledRule_IsNotReported()
    {
        WriteLoadInsideLoopFile();
        WriteSettings("""{ "rules": { "LoadInsideLoop": { "enabled": false } } }""");

        var findings = CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog);

        Assert.DoesNotContain(findings, f => f.Rule == "LoadInsideLoop");
    }

    [Fact]
    public void SeverityOverride_IsApplied()
    {
        WriteLoadInsideLoopFile();
        WriteSettings("""{ "rules": { "LoadInsideLoop": { "severity": "Medium" } } }""");

        var findings = CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog);

        var finding = Assert.Single(findings, f => f.Rule == "LoadInsideLoop");
        Assert.Equal(Severity.Medium, finding.Severity);
    }

    [Fact]
    public void NoSettingsFile_BehaviorUnchanged()
    {
        WriteLoadInsideLoopFile();

        var findings = CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog);

        var finding = Assert.Single(findings, f => f.Rule == "LoadInsideLoop");
        Assert.Equal(Severity.High, finding.Severity);
    }

    [Fact]
    public void MalformedSettingsFile_ThrowsCliException()
    {
        WriteLoadInsideLoopFile();
        WriteSettings("{ not valid json !!");

        Assert.Throws<CliException>(() => CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog));
    }

    [Fact]
    public void InvalidSeverityValue_ThrowsCliException()
    {
        WriteLoadInsideLoopFile();
        WriteSettings("""{ "rules": { "LoadInsideLoop": { "severity": "Catastrophic" } } }""");

        Assert.Throws<CliException>(() => CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog));
    }

    [Fact]
    public void CustomMethodCallInLoopRule_Fires()
    {
        WriteOrchestrationFile("Foo.cs", """
            public class Foo
            {
                public void DoWork()
                {
                    foreach (var item in items)
                    {
                        fooService.SyncWidget(item);
                    }
                }
            }
            """);
        WriteSettings("""
            {
              "customRules": [
                {
                  "name": "SyncWidgetInsideLoop",
                  "kind": "methodCallInLoop",
                  "methodNames": ["SyncWidget"],
                  "receiverContains": "fooService",
                  "actionNoun": "Widget sync",
                  "insideLoopFix": "Batch widget syncs."
                }
              ]
            }
            """);

        var findings = CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog);

        var finding = Assert.Single(findings, f => f.Rule == "SyncWidgetInsideLoop");
        Assert.Equal(7, finding.LineNumber);
        Assert.Equal(Severity.High, finding.Severity);
        Assert.Equal("Batch widget syncs.", finding.SuggestedFix);
    }

    [Fact]
    public void CustomMethodCallInLoopRule_ReceiverMismatch_DoesNotFire()
    {
        WriteOrchestrationFile("Foo.cs", """
            public class Foo
            {
                public void DoWork()
                {
                    foreach (var item in items)
                    {
                        otherThing.SyncWidget(item);
                    }
                }
            }
            """);
        WriteSettings("""
            {
              "customRules": [
                { "name": "SyncWidgetInsideLoop", "kind": "methodCallInLoop", "methodNames": ["SyncWidget"], "receiverContains": "fooService" }
              ]
            }
            """);

        var findings = CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog);

        Assert.DoesNotContain(findings, f => f.Rule == "SyncWidgetInsideLoop");
    }

    [Fact]
    public void CustomEntityPropertyAccessRule_Fires()
    {
        WriteOrchestrationFile("Foo.cs", """
            public class Foo
            {
                public void DoWork()
                {
                    var name = x.Material.WidgetCode;
                }
            }
            """);
        WriteSettings("""
            {
              "customRules": [
                {
                  "name": "WidgetCodeAccess",
                  "kind": "entityPropertyAccess",
                  "propertyNames": ["WidgetCode"],
                  "severity": "Critical",
                  "description": "WidgetCode dereferenced.",
                  "suggestedFix": "Do not."
                }
              ]
            }
            """);

        var findings = CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog);

        var finding = Assert.Single(findings, f => f.Rule == "WidgetCodeAccess");
        Assert.Equal(Severity.Critical, finding.Severity);
    }

    [Fact]
    public void CustomRule_UnknownKind_ThrowsCliException()
    {
        WriteLoadInsideLoopFile();
        WriteSettings("""{ "customRules": [ { "name": "X", "kind": "banana" } ] }""");

        Assert.Throws<CliException>(() => CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog));
    }

    [Fact]
    public void SuppressionComment_SameLine_SuppressesFinding()
    {
        WriteOrchestrationFile("Foo.cs", """
            public class Foo
            {
                public void DoWork()
                {
                    foreach (var item in items)
                    {
                        entity.Load(); // codereview-disable-line
                    }
                }
            }
            """);

        var findings = CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog);

        Assert.DoesNotContain(findings, f => f.Rule == "LoadInsideLoop");
    }

    [Fact]
    public void SuppressionComment_NextLine_WithRuleName_SuppressesOnlyThatRule()
    {
        WriteOrchestrationFile("Foo.cs", """
            public class Foo
            {
                public void DoWork()
                {
                    foreach (var item in items)
                    {
                        // codereview-disable-next-line LoadInsideLoop
                        var x = entity.Load(item.Material.Description);
                    }
                }
            }
            """);

        var findings = CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog);

        Assert.DoesNotContain(findings, f => f.Rule == "LoadInsideLoop");
        Assert.Contains(findings, f => f.Rule == "ExcessiveLazyLoading" && f.LineNumber == 8);
    }

    [Fact]
    public void SuppressionComment_DifferentRuleName_DoesNotSuppress()
    {
        WriteOrchestrationFile("Foo.cs", """
            public class Foo
            {
                public void DoWork()
                {
                    foreach (var item in items)
                    {
                        entity.Load(); // codereview-disable-line SomeOtherRule
                    }
                }
            }
            """);

        var findings = CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog);

        Assert.Contains(findings, f => f.Rule == "LoadInsideLoop" && f.LineNumber == 7);
    }

    [Fact]
    public void Baseline_MatchingFinding_IsExcluded_AndNewFindingStillReported()
    {
        WriteLoadInsideLoopFile();

        // Accept current findings as the baseline.
        var initial = CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog);
        Assert.Contains(initial, f => f.Rule == "LoadInsideLoop");
        CodeReviewBaseline.Write(_tempRoot, initial);

        // Baselined finding is gone on the next scan.
        Assert.DoesNotContain(CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog), f => f.Rule == "LoadInsideLoop");

        // A new finding in another file is still reported, and applyBaseline: false sees everything.
        WriteOrchestrationFile("Bar.cs", """
            public class Bar
            {
                public void DoWork()
                {
                    foreach (var item in items)
                    {
                        other.Resolve();
                    }
                }
            }
            """);

        var afterNewCode = CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog);
        Assert.DoesNotContain(afterNewCode, f => f.Rule == "LoadInsideLoop");
        Assert.Contains(afterNewCode, f => f.Rule == "ResolveInsideLoop");

        var unfiltered = CmFrameworkScanner.GetFindings(_tempRoot, applyBaseline: false, entityCatalog: TestEntities.Catalog);
        Assert.Contains(unfiltered, f => f.Rule == "LoadInsideLoop");
    }

    [Fact]
    public void Baseline_SurvivesLineNumberDrift()
    {
        WriteLoadInsideLoopFile();
        CodeReviewBaseline.Write(_tempRoot, CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog));

        // Shift the flagged call down by inserting lines above it — the baseline matches on
        // file + rule + snippet, so the finding must stay excluded despite the new line number.
        WriteOrchestrationFile("Foo.cs", """
            public class Foo
            {
                // a comment
                // another comment
                public void DoWork()
                {
                    foreach (var item in items)
                    {
                        entity.Load();
                    }
                }
            }
            """);

        Assert.DoesNotContain(CmFrameworkScanner.GetFindings(_tempRoot, entityCatalog: TestEntities.Catalog), f => f.Rule == "LoadInsideLoop");
    }
}
