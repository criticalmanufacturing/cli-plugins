using System.Text.Json;
using Cmf.Cli.Plugin.CodeReview;
using Cmf.Cli.Plugin.CodeReview.Enums;
using Xunit;

namespace codereview.tests;

public class FindingActionGroupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FindingRetainsEveryGroupAndAlignedSourceAcrossFormats(bool removed)
    {
        using var fixture = new ActionGroupFixture();
        var root = fixture.AddPackage();
        fixture.Write($"{root}/1.0/orders.json", ActionGroupFixture.Orders(("SharedDee", "A.Pre"), ("SharedDee", "B.Post"), ("SharedDee", "C.Pre")));
        fixture.Write("Cmf.Sample.Data/DEEs/SharedDee.cs", """
            public class SharedDee : DeeDevBase {
                public override object DeeActionCode(object input) {
                    foreach (var item in items) { item.Load(); }
                    return input;
                }
            }
            """);
        if (removed) fixture.Write("Cmf.Sample.Data/DEEs/ProcessRules/1.0/Before/Remove.cs",
            "class Migration { void Run() { action.Name = \"SharedDee\"; action.RemoveActionGroups(action.ActionGroupActions); } }");
        var finding = Assert.Single(CmFrameworkScanner.GetFindings(fixture.Root, null, null, null, true, TestEntities.Catalog)
            .Where(f => f.Rule == "LoadInsideLoop"));
        if (removed) Assert.Null(finding.ActionGroups);
        else
        {
            Assert.Equal(new[] { "A.Pre", "B.Post", "C.Pre" }, finding.ActionGroups);
            Assert.Equal(3, finding.ActionGroupSources!.Count);
            Assert.All(finding.ActionGroupSources, source => Assert.EndsWith("1.0/orders.json", source));
            var reports = new[] {
                CmReportGenerator.BuildStandardReport([finding], "title", "summary"),
                CmReportGenerator.BuildDetailedReport([finding], "title", "summary"),
                CmHtmlReportGenerator.BuildStandardReport([finding], "title", "summary"),
                CmHtmlReportGenerator.BuildDetailedReport([finding], "title", "summary") };
            Assert.All(reports, report => Assert.Contains("A.Pre, B.Post, C.Pre", report));
            using var sarif = JsonDocument.Parse(CmSarifReportGenerator.BuildStandardReport([finding], "title", "summary"));
            Assert.Equal(3, sarif.RootElement.GetProperty("runs")[0].GetProperty("results")[0].GetProperty("properties").GetProperty("actionGroups").GetArrayLength());
        }
        var roundtrip = JsonSerializer.Deserialize<List<Cmf.Cli.Plugin.CodeReview.Models.Finding>>(JsonSerializer.Serialize(new[] { finding }))!;
        Assert.Equal(finding.ActionGroups, Assert.Single(roundtrip).ActionGroups);
    }
}
