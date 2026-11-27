using System.Text.Json;
using Cmf.Cli.Plugin.CodeReview;
using Cmf.Cli.Plugin.CodeReview.Commands;
using Cmf.Cli.Plugin.CodeReview.Enums;
using Cmf.Cli.Plugin.CodeReview.Models;
using Xunit;

namespace codereview.tests;

public class ActionGroupOriginTests : IDisposable
{
    private readonly ActionGroupFixture _fixture = new();
    private const string Common = "Cmf.Sample.Business/Cmf.Sample.Common";

    public ActionGroupOriginTests() => _fixture.Write("Cmf.Sample.Business/cmfpackage.json", """{"packageType":"Business"}""");
    public void Dispose() => _fixture.Dispose();

    private static List<ActionGroupSummary> Summaries((HashSet<string> TypeNames, HashSet<string> MethodNames) names,
        params string[] groups) => MasterDataActionGroupResolver.Summarize(
            new Dictionary<string, List<ActionGroupInfo>>
            {
                ["Dee"] = groups.Select(group => new ActionGroupInfo { ActionGroup = group, SourceLocation = "orders.json" }).ToList()
            }, declaredTypeNames: names.TypeNames, declaredMethodNames: names.MethodNames);

    [Theory]
    [InlineData("C:\\repo\\Common\\Constants\\Operations\\Example.cs", true)]
    [InlineData("/repo/Common/Constants/Operations/Example.cs", true)]
    [InlineData("src\\Constants/Operations\\Example.cs", true)]
    [InlineData("Constants/Example.cs", true)]
    [InlineData("Constants\\Example.cs", true)]
    [InlineData("/repo/Implementation/Constants.cs", false)]
    [InlineData("/repo/MyConstants/Example.cs", false)]
    [InlineData("/repo/ConstantsHelpers/Example.cs", false)]
    [InlineData("/repo/constants/Example.cs", false)]
    public void ConstantsRecognition_UsesExactDirectorySegmentsWithEitherSeparator(string path, bool expected) =>
        Assert.Equal(expected, CodeReviewPaths.IsConstantsFile(path));

    [Fact]
    public void ConstantsClassAndMethodCollision_DoesNotClassifyPlatformGroupAsCustom()
    {
        // Generic reproducer of a customer metadata holder sharing a platform implementation's name.
        _fixture.Write($"{Common}/Constants/Operations/PlatformOperations.cs", """
            public static class PlatformOperations
            {
                public const string Group = "Platform.PlatformOperations.MoveItems.Post";
                public static string MoveItems() => Group;
            }
            """);
        _fixture.Write($"{Common}/Implementation/CustomerOperations.cs", """
            public class CustomerOperations { public void CustomMoveItems() {} }
            """);
        var names = MasterDataActionGroupResolver.CollectDeclaredNames(_fixture.Root);
        var summaries = Summaries(names, "Platform.PlatformOperations.MoveItems.Post", "Custom.CustomerOperations.CustomMoveItems.Pre");
        Assert.Equal(ActionGroupOrigin.Oob, summaries.Single(summary => summary.ActionGroup.StartsWith("Platform.")).Origin);
        Assert.DoesNotContain("PlatformOperations", names.TypeNames);
        Assert.DoesNotContain("MoveItems", names.MethodNames);
        Assert.Equal(ActionGroupOrigin.Custom, summaries.Single(summary => summary.ActionGroup.StartsWith("Custom.")).Origin);
    }

    [Theory]
    [InlineData("Constants/Operations", false)]
    [InlineData("MyConstants/Operations", true)]
    [InlineData("ConstantsHelpers", true)]
    [InlineData("Implementations", true)]
    public void GenuineImplementationsWithSimilarNames_AreStillCollected(string directory, bool included)
    {
        _fixture.Write($"{Common}/{directory}/Operations.cs", """public class Operations { public void CustomWork() {} }""");
        var names = MasterDataActionGroupResolver.CollectDeclaredNames(_fixture.Root);
        Assert.Equal(included, names.TypeNames.Contains("Operations"));
        Assert.Equal(included, names.MethodNames.Contains("CustomWork"));
    }

    [Fact]
    public void GenuineTypeOutsideConstants_StillWinsForTheSameNameAndLifecycleVerb()
    {
        _fixture.Write($"{Common}/Constants/PlatformOperations.cs", """public class PlatformOperations { public void StubOnly() {} }""");
        _fixture.Write($"{Common}/Implementation/PlatformOperations.cs", """public class PlatformOperations { public void Save() {} }""");
        var names = MasterDataActionGroupResolver.CollectDeclaredNames(_fixture.Root);
        Assert.DoesNotContain("StubOnly", names.MethodNames);
        Assert.Equal(ActionGroupOrigin.Custom, Assert.Single(Summaries(names, "Custom.PlatformOperations.Save.Post")).Origin);
    }

    [Fact]
    public void BothSetsMatchWrappersAndReuseTheSuppliedCache()
    {
        var file = _fixture.Write($"{Common}/Types.cs", """
            public interface IWork { void Work(); }
            public record WorkRecord { public void Run() {} }
            public struct Value { public void Convert() {} }
            public class Outer
            {
                public Outer() {}
                public void Execute() { void LocalOnly() {} }
                public class Inner { public void Execute() {} }
            }
            """);
        _fixture.AddPackage();
        _fixture.Write("Cmf.Sample.Data/DEEs/Dee.cs", """public class Dee { public void DeeActionCode() {} }""");
        _fixture.Write("Outside/NotScanned.cs", """public class Outside { public void NotInScope() {} }""");
        _fixture.Write($"{Common}/Constants/Hidden.cs", """public class Hidden { public void HiddenMethod() {} }""");
        file = Path.GetFullPath(file);
        var cache = new CodeReviewFileCache();
        _ = cache.Get(file).Root;
        File.WriteAllText(file, "public class Replacement { public void ReplacementMethod() {} }");
        var names = MasterDataActionGroupResolver.CollectDeclaredNames(_fixture.Root, cache);
        Assert.Equal(new[] { "Dee", "IWork", "Inner", "Outer", "Value", "WorkRecord" }, names.TypeNames.OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal(new[] { "Convert", "DeeActionCode", "Execute", "Run", "Work" }, names.MethodNames.OrderBy(name => name, StringComparer.Ordinal));
        Assert.True(names.TypeNames.SetEquals(MasterDataActionGroupResolver.CollectDeclaredTypeNames(_fixture.Root, cache)));
        Assert.True(names.MethodNames.SetEquals(MasterDataActionGroupResolver.CollectDeclaredMethodNames(_fixture.Root, cache)));
        Assert.Contains("Replacement", MasterDataActionGroupResolver.CollectDeclaredNames(_fixture.Root).TypeNames);
    }

    [Theory]
    [InlineData("Create")]
    [InlineData("Save")]
    [InlineData("Terminate")]
    [InlineData("Load")]
    [InlineData("Delete")]
    [InlineData("CreateEntity")]
[InlineData("CreateVersion")]
[InlineData("TryLoad")]
[InlineData("LoadRelations")]
[InlineData("RemoveRelations")]
[InlineData("AddRelations")]
[InlineData("AddRelatedRelations")]
[InlineData("RemoveRelatedRelations")]
[InlineData("ObjectExists")]
[InlineData("GetVersions")]
[InlineData("Clone")]
[InlineData("ToString")]
[InlineData("Equals")]
[InlineData("GetHashCode")]
[InlineData("GetType")]
    public void LifecycleGuard_OnlyRestrictsMethodFallback(string verb)
    {
        var names = (new HashSet<string>(StringComparer.Ordinal) { "RealImplementation" },
            new HashSet<string>(StringComparer.Ordinal) { verb, "CustomDistinctiveWork" });
        var summaries = Summaries(names, $"Platform.UnrelatedType.{verb}.Post", $"Custom.RealImplementation.{verb}.Pre",
            "Custom.RuntimeModule.CustomDistinctiveWork.Post");
        Assert.Equal(ActionGroupOrigin.Oob, summaries.Single(summary => summary.ActionGroup.StartsWith("Platform.")).Origin);
        Assert.All(summaries.Where(summary => summary.ActionGroup.StartsWith("Custom.")), summary => Assert.Equal(ActionGroupOrigin.Custom, summary.Origin));
    }

    [Fact]
    public void AbsentNameIndex_IsUnknownAndEmptyIndexIsOob()
    {
        var map = new Dictionary<string, List<ActionGroupInfo>>
        {
            ["Dee"] = [new() { ActionGroup = "Platform.Type.Work.Post", SourceLocation = "a.json" }]
        };
        Assert.Equal(ActionGroupOrigin.Unknown, Assert.Single(MasterDataActionGroupResolver.Summarize(map)).Origin);
        Assert.Equal(ActionGroupOrigin.Oob, Assert.Single(MasterDataActionGroupResolver.Summarize(map, declaredTypeNames: new HashSet<string>())).Origin);
    }

    [Theory]
    [InlineData(ReportMode.Standard, ReportFormat.Markdown)]
    [InlineData(ReportMode.Detailed, ReportFormat.Markdown)]
    [InlineData(ReportMode.Standard, ReportFormat.Html)]
    [InlineData(ReportMode.Detailed, ReportFormat.Html)]
    [InlineData(ReportMode.Standard, ReportFormat.Sarif)]
    [InlineData(ReportMode.Detailed, ReportFormat.Sarif)]
    public void ClassifiedOrigin_RoundTripsThroughEveryReportWithoutSource(ReportMode mode, ReportFormat format)
    {
        var root = _fixture.AddPackage();
        _fixture.Write($"{root}/1.0/orders.json", ActionGroupFixture.Orders(("Dee", "Platform.PlatformOperations.MoveItems.Post"),
            ("Dee", "Custom.CustomerOperations.Save.Pre")));
        _fixture.Write($"{Common}/Constants/PlatformOperations.cs", """public class PlatformOperations { public void MoveItems() {} }""");
        var implementation = _fixture.Write($"{Common}/Implementation/CustomerOperations.cs", """public class CustomerOperations { public void Save() {} }""");
        var findings = CmFrameworkScanner.GetFindings(_fixture.Root, null, null, null, true, TestEntities.Catalog, out var summaries);
        var path = _fixture.Write("findings.json", FindingsIo.Serialize(findings, summaries));
        File.Delete(implementation);
        var roundTrip = FindingsIo.Deserialize(File.ReadAllText(path)).ActionGroupSummaries!;
        Assert.Equal(ActionGroupOrigin.Custom, roundTrip.Single(summary => summary.ActionGroup.StartsWith("Custom.")).Origin);
        Assert.Equal(ActionGroupOrigin.Oob, roundTrip.Single(summary => summary.ActionGroup.StartsWith("Platform.")).Origin);
        var report = ReportCommand.BuildContent(path, mode, format, "Review", "Summary", _fixture.Root);
        if (format == ReportFormat.Sarif)
        {
            using var doc = JsonDocument.Parse(report);
            var recorded = doc.RootElement.GetProperty("runs")[0].GetProperty("properties").GetProperty("actionGroupSummaries");
            Assert.Contains(recorded.EnumerateArray(), summary => summary.GetProperty("Origin").GetString() == "Custom");
            Assert.Contains(recorded.EnumerateArray(), summary => summary.GetProperty("Origin").GetString() == "Oob");
        }
        else if (format == ReportFormat.Html)
        {
            Assert.Contains("<th>Origin</th>", report);
            Assert.Contains("<td>Custom</td>", report);
            Assert.Contains("<td>Oob</td>", report);
        }
        else
        {
            Assert.Contains("Custom.CustomerOperations.Save.Pre | 1 | Low | Custom |", report);
            Assert.Contains("Platform.PlatformOperations.MoveItems.Post | 1 | Low | Oob |", report);
        }
    }

    [Fact]
    public void LegacyReportFallback_DoesNotInferOriginWithoutASourceIndex()
    {
        var root = _fixture.AddPackage();
        _fixture.Write($"{root}/1.0/orders.json", ActionGroupFixture.Orders(("Dee", "Custom.CustomerOperations.Save.Pre")));
        _fixture.Write($"{Common}/Implementation/CustomerOperations.cs", """public class CustomerOperations { public void Save() {} }""");
        var path = _fixture.Write("legacy.json", "[]");
        using var report = JsonDocument.Parse(ReportCommand.BuildContent(path, ReportMode.Standard, ReportFormat.Sarif, "Review", "Summary", _fixture.Root));
        var summary = Assert.Single(report.RootElement.GetProperty("runs")[0].GetProperty("properties").GetProperty("actionGroupSummaries").EnumerateArray());
        Assert.Equal("Unknown", summary.GetProperty("Origin").GetString());
        var note = report.RootElement.GetProperty("runs")[0].GetProperty("properties").GetProperty("actionGroupSummaryNote").GetString();
        Assert.Contains("Origin remains Unknown", note);
        Assert.Contains("declaration indexes are unavailable", note);
    }

    [Theory]
    [InlineData(ReportMode.Standard, ReportFormat.Markdown)]
    [InlineData(ReportMode.Detailed, ReportFormat.Markdown)]
    [InlineData(ReportMode.Standard, ReportFormat.Html)]
    [InlineData(ReportMode.Detailed, ReportFormat.Html)]
    public void LegacyReport_ExplainsUnknownOriginInHumanReadableFormats(ReportMode mode, ReportFormat format)
    {
        var path = _fixture.Write("legacy.json", "[]");
        var report = ReportCommand.BuildContent(path, mode, format, "Review", "Summary", _fixture.Root);
        Assert.Contains("Origin remains Unknown", report);
        Assert.Contains("declaration indexes are unavailable", report);
        Assert.Contains("scan --include-action-group-summary", report);
    }

    [Fact]
    public void ScanUsesWholeRepositoryOriginEvidence_WhenFindingsAreScopedFilteredOrSuppressed()
    {
        var root = _fixture.AddPackage();
        _fixture.Write($"{root}/1.0/orders.json", ActionGroupFixture.Orders(("Dee", "Platform.PlatformOperations.MoveItems.Post"),
            ("Dee", "Custom.CustomerOperations.Save.Pre"), ("Dee", "Custom.RuntimeModule.DistinctiveWork.Post")));
        _fixture.Write($"{Common}/Constants/PlatformOperations.cs", """
            public class PlatformOperations { public void MoveItems() { foreach (var item in items) { item.Load(); } } }
            """);
        _fixture.Write($"{Common}/Implementation/CustomerOperations.cs", """public class CustomerOperations { public void Save() {} public void DistinctiveWork() {} }""");
        var findings = CmFrameworkScanner.GetFindings(_fixture.Root, null, null, null, true, TestEntities.Catalog, out var summaries);
        // Constants are excluded only from Origin evidence, not from ordinary rule analysis.
        Assert.Contains(findings, finding => finding.Rule == "LoadInsideLoop" && finding.RelativePath.Contains("Constants"));
        Assert.Equal(ActionGroupOrigin.Oob, summaries.Single(summary => summary.ActionGroup.StartsWith("Platform.")).Origin);
        Assert.All(summaries.Where(summary => summary.ActionGroup.StartsWith("Custom.")), summary => Assert.Equal(ActionGroupOrigin.Custom, summary.Origin));
        var filtered = CmFrameworkScanner.GetFindings(_fixture.Root, ["Cmf.Sample.Data/DEEs"], [], null, true, TestEntities.Catalog, out var filteredSummaries);
        Assert.Empty(filtered);
        Assert.Equal(JsonSerializer.Serialize(summaries), JsonSerializer.Serialize(filteredSummaries));
        CodeReviewBaseline.Write(_fixture.Root, findings);
        Assert.Empty(CmFrameworkScanner.GetFindings(_fixture.Root, null, null, null, true, TestEntities.Catalog, out var baselineSummaries));
        Assert.Equal(JsonSerializer.Serialize(summaries), JsonSerializer.Serialize(baselineSummaries));
    }
}
