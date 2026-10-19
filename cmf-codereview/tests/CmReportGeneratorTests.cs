using Cmf.Cli.Plugin.CodeReview;
using Cmf.Cli.Plugin.CodeReview.Models;
using Cmf.Cli.Plugin.CodeReview.Enums;
using Xunit;

namespace codereview.tests;

public class CmReportGeneratorTests
{
    private static Finding MakeFinding(Severity severity, string rule, int line = 1, string relativePath = "Foo.cs") =>
        new()
        {
            FilePath = @"C:\repo\" + relativePath,
            RelativePath = relativePath,
            LineNumber = line,
            Severity = severity,
            Rule = rule,
            Description = $"{rule} description",
            Snippet = "entity.Load();",
            SuggestedFix = $"Fix for {rule}"
        };

    private static List<Finding> SampleFindings() => new()
    {
        MakeFinding(Severity.Critical, "LoadInsideLoop", line: 10),
        MakeFinding(Severity.High, "ExcessiveLazyLoading", line: 20),
        MakeFinding(Severity.Medium, "ConfigGetConfig", line: 30)
    };

    [Fact]
    public void BuildStandardReport_ContainsTitleSummaryAndAllFindingRuleNames()
    {
        var report = CmReportGenerator.BuildStandardReport(SampleFindings(), "My Review Title", "This is the summary.");

        Assert.Contains("My Review Title", report);
        Assert.Contains("This is the summary.", report);
        Assert.Contains("LoadInsideLoop", report);
        Assert.Contains("ExcessiveLazyLoading", report);
        Assert.Contains("ConfigGetConfig", report);
    }

    [Fact]
    public void BuildStandardReport_HasSeveritySectionHeaders()
    {
        var report = CmReportGenerator.BuildStandardReport(SampleFindings(), "Title", "Summary");

        Assert.Contains("CRITICAL", report);
        Assert.Contains("HIGH", report);
        Assert.Contains("MEDIUM", report);
    }

    [Fact]
    public void BuildStandardReport_EmptyFindings_ShowsNoneForEachSeveritySection()
    {
        var report = CmReportGenerator.BuildStandardReport(new List<Finding>(), "Title", "Summary");

        Assert.Contains("| None | None | None | None | None | None |", report);
    }

    [Fact]
    public void BuildStandardReport_FindingRelativePath_AppearsInOutput()
    {
        var findings = new List<Finding> { MakeFinding(Severity.Critical, "LoadInsideLoop", line: 42, relativePath: "Some/Path.cs") };

        var report = CmReportGenerator.BuildStandardReport(findings, "Title", "Summary");

        Assert.Contains("Some/Path.cs (line 42)", report);
    }

    [Fact]
    public void BuildDetailedReport_ContainsTitleSummaryAndRuleSummaryTable()
    {
        var report = CmReportGenerator.BuildDetailedReport(SampleFindings(), "My Review Title", "This is the summary.");

        Assert.Contains("My Review Title", report);
        Assert.Contains("This is the summary.", report);
        Assert.Contains("Rule Summary", report);
        Assert.Contains("LoadInsideLoop", report);
        Assert.Contains("ExcessiveLazyLoading", report);
        Assert.Contains("ConfigGetConfig", report);
    }

    [Fact]
    public void BuildDetailedReport_EscalatableRuleAtCriticalSeverity_IncludesEscalationReasonClause()
    {
        var findings = new List<Finding>
        {
            new()
            {
                FilePath = @"C:\repo\Foo.cs",
                RelativePath = "Foo.cs",
                LineNumber = 5,
                Severity = Severity.Critical,
                Rule = "LoadInsideLoop",
                Description = "This is inside a nested loop (2 levels deep) here.",
                Snippet = "entity.Load();",
                SuggestedFix = "Batch it."
            }
        };

        var report = CmReportGenerator.BuildDetailedReport(findings, "Title", "Summary");

        Assert.Contains("escalated from High", report);
        Assert.Contains("nested loop", report);
    }

    [Fact]
    public void BuildDetailedReport_EmptyFindings_ShowsNoFindingsPerSeverity()
    {
        var report = CmReportGenerator.BuildDetailedReport(new List<Finding>(), "Title", "Summary");

        Assert.Contains("No findings.", report);
    }
}
