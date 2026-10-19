using Cmf.Cli.Plugin.CodeReview;
using Cmf.Cli.Plugin.CodeReview.Models;
using Cmf.Cli.Plugin.CodeReview.Enums;
using Xunit;

namespace codereview.tests;

public class CmHtmlReportGeneratorTests
{
    private static Finding MakeFinding(Severity severity, string rule, int line = 1, string relativePath = "Foo.cs", string? snippet = null) =>
        new()
        {
            FilePath = @"C:\repo\" + relativePath,
            RelativePath = relativePath,
            LineNumber = line,
            Severity = severity,
            Rule = rule,
            Description = $"{rule} description",
            Snippet = snippet ?? "entity.Load();",
            SuggestedFix = $"Fix for {rule}"
        };

    private static List<Finding> SampleFindings() => new()
    {
        MakeFinding(Severity.Critical, "LoadInsideLoop", line: 10),
        MakeFinding(Severity.High, "ExcessiveLazyLoading", line: 20),
        MakeFinding(Severity.Medium, "ConfigGetConfig", line: 30)
    };

    [Fact]
    public void BuildDetailedReport_IsSelfContainedHtmlDocument()
    {
        var html = CmHtmlReportGenerator.BuildDetailedReport(SampleFindings(), "My Review Title", "This is the summary.");

        Assert.StartsWith("<!doctype html>", html);
        Assert.Contains("</html>", html);
        Assert.Contains("<style>", html);
        Assert.Contains("<script>", html);
        // Self-contained: no external asset references.
        Assert.DoesNotContain("http://", html);
        Assert.DoesNotContain("https://", html);
        Assert.DoesNotContain("src=", html);
    }

    [Fact]
    public void BuildDetailedReport_IncludesColumnSortAndResizeBehaviour()
    {
        var html = CmHtmlReportGenerator.BuildDetailedReport(SampleFindings(), "Title", "Summary");

        Assert.Contains("function sortTable", html);   // click-to-sort wiring
        Assert.Contains("col-resize", html);           // drag-to-resize handle
        Assert.Contains("table-layout:fixed", html);   // predictable column widths
    }

    [Fact]
    public void BuildDetailedReport_ContainsTitleSummaryRuleSummaryAndAllRuleNames()
    {
        var html = CmHtmlReportGenerator.BuildDetailedReport(SampleFindings(), "My Review Title", "This is the summary.");

        Assert.Contains("My Review Title", html);
        Assert.Contains("This is the summary.", html);
        Assert.Contains("Rule Summary", html);
        Assert.Contains("LoadInsideLoop", html);
        Assert.Contains("ExcessiveLazyLoading", html);
        Assert.Contains("ConfigGetConfig", html);
    }

    [Fact]
    public void BuildDetailedReport_RendersCollapsibleRuleSectionsWithSeverityClasses()
    {
        var html = CmHtmlReportGenerator.BuildDetailedReport(SampleFindings(), "Title", "Summary");

        Assert.Contains("<details class=\"rule crit\"", html);
        Assert.Contains("<details class=\"rule high\"", html);
        Assert.Contains("<details class=\"rule med\"", html);
        Assert.Contains("class=\"badge crit\"", html);
    }

    [Fact]
    public void BuildDetailedReport_EscalatableRuleAtCritical_IncludesEscalationReasonClause()
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

        var html = CmHtmlReportGenerator.BuildDetailedReport(findings, "Title", "Summary");

        Assert.Contains("escalated from High", html);
        Assert.Contains("nested loop", html);
    }

    [Fact]
    public void BuildDetailedReport_EmptyFindings_ShowsNoFindings()
    {
        var html = CmHtmlReportGenerator.BuildDetailedReport(new List<Finding>(), "Title", "Summary");

        Assert.Contains("No findings.", html);
    }

    [Fact]
    public void BuildStandardReport_HasSeveritySectionHeadersAndRuleTaggedDescription()
    {
        var html = CmHtmlReportGenerator.BuildStandardReport(SampleFindings(), "Title", "Summary");

        Assert.Contains("CRITICAL", html);
        Assert.Contains("HIGH", html);
        Assert.Contains("MEDIUM", html);
        // Standard rows fold the rule tag and suggested fix into the description column.
        Assert.Contains("[LoadInsideLoop]", html);
        Assert.Contains("Fix for LoadInsideLoop", html);
    }

    [Fact]
    public void BuildStandardReport_EmptyFindings_ShowsNonePerSeverity()
    {
        var html = CmHtmlReportGenerator.BuildStandardReport(new List<Finding>(), "Title", "Summary");

        Assert.Contains("<td colspan=\"6\">None</td>", html);
    }

    [Fact]
    public void SnippetWithHtmlMetacharacters_IsEncodedNotInjected()
    {
        var findings = new List<Finding> { MakeFinding(Severity.Critical, "LoadInsideLoop", snippet: "if (a < b && c > d) x.Load(\"<script>\");") };

        var html = CmHtmlReportGenerator.BuildDetailedReport(findings, "Title", "Summary");

        // The snippet's metacharacters are HTML-encoded, so the raw snippet is never injected as markup.
        Assert.DoesNotContain("x.Load(\"<script>\")", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("a &lt; b &amp;&amp; c &gt; d", html);
    }
}
