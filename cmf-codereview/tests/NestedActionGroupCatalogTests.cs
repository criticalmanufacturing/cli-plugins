using Cmf.Cli.Plugin.CodeReview;
using Cmf.Cli.Plugin.CodeReview.Models;
using Xunit;

namespace codereview.tests;

public class NestedActionGroupCatalogTests
{
    [Fact]
    public void SummariesAndAllReports_RetainTransitiveCatalogKnowledge()
    {
        var map = new Dictionary<string, List<ActionGroupInfo>>
        {
            ["ExampleDee"] = [new() { ActionGroup = "Platform.MaterialManagement.ComplexTrackOutMaterials.Pre", SourceLocation = "orders.json" }]
        };
        var summaries = MasterDataActionGroupResolver.Summarize(map,
            nestedActionGroupEdges: NestedActionGroupCatalog.BuildEdgeMap(NestedActionGroupCatalog.LoadDefaults()));
        Assert.Contains("DataCollectionInstance.PerformImmediate", Assert.Single(summaries).TriggeredActionGroups);
        var reports = new[]
        {
            CmReportGenerator.BuildStandardReport([], "title", "summary", summaries),
            CmReportGenerator.BuildDetailedReport([], "title", "summary", summaries),
            CmHtmlReportGenerator.BuildStandardReport([], "title", "summary", summaries),
            CmHtmlReportGenerator.BuildDetailedReport([], "title", "summary", summaries),
            CmSarifReportGenerator.BuildStandardReport([], "title", "summary", summaries)
        };
        Assert.All(reports, report => Assert.Contains("DataCollectionInstance.PerformImmediate", report));
        var recorded = System.Text.Json.JsonSerializer.Serialize(summaries);
        Assert.Contains("TriggeredActionGroups", recorded);
    }

    [Fact]
    public void LoadDefaults_ResolvesConfirmedTrackOutChainTransitively()
    {
        // Sanity check on the shipped embedded resource -- if this ever fails to load or comes back
        // empty, every dependent feature (Summarize's TriggeredActionGroups) silently degrades to "no
        // known nested chains" rather than erroring, so it's worth a direct assertion here. The chain is
        // stored as direct edges (ComplexTrackOutMaterials -> TrackOutMaterials -> MaterialCollection.TrackOut
        // -> MaterialCollection.MoveToNextStep, plus a separate EDC data-collection edge off
        // ComplexTrackOutMaterials itself), resolved transitively -- not hand-flattened into one entry.
        var defaults = NestedActionGroupCatalog.LoadDefaults();
        var map = NestedActionGroupCatalog.BuildEdgeMap(defaults);

        var triggered = NestedActionGroupCatalog.ResolveTransitiveTriggered("ComplexTrackOutMaterials", map);

        Assert.Contains("TrackOutMaterials", triggered);
        Assert.Contains("MaterialCollection.TrackOut", triggered);
        Assert.Contains("MaterialCollection.MoveToNextStep", triggered);
        Assert.Contains("DataCollectionInstanceOrchestration.PerformImmediateDataCollection", triggered);
        // Regression: a cross-class edge referencing "DataCollectionInstanceOrchestration.PerformImmediateDataCollection"
        // (the prefixed form the cross-class sweep used) is a DIFFERENT dictionary key from the bare
        // "PerformImmediateDataCollection" the EdcManagement sweep used for the same method -- without a
        // bridging entry for the prefixed key, the walk dead-ended there and silently dropped the real
        // terminal BO wrap. Confirmed live: ComplexDispatchAndTrackInMaterials.Pre's TriggeredActionGroups
        // included the intermediate node but not this leaf.
        Assert.Contains("DataCollectionInstance.PerformImmediate", triggered);
    }

    [Fact]
    public void LoadDefaults_ResolvesConfirmedDispatchAndTrackInChainTransitively()
    {
        // Mirrors the real confirmed case that caught the bridging-entry bug above:
        // ComplexDispatchAndTrackInMaterials transitively reaches DataCollectionInstance.PerformImmediate
        // via a cross-class edge (ComplexTrackInMaterials -> DataCollectionInstanceOrchestration.PerformImmediateDataCollection),
        // not just the intermediate orchestration-class node.
        var defaults = NestedActionGroupCatalog.LoadDefaults();
        var map = NestedActionGroupCatalog.BuildEdgeMap(defaults);

        var triggered = NestedActionGroupCatalog.ResolveTransitiveTriggered("ComplexDispatchAndTrackInMaterials", map);

        Assert.Contains("DataCollectionInstanceOrchestration.PerformImmediateDataCollection", triggered);
        Assert.Contains("DataCollectionInstance.PerformImmediate", triggered);
        Assert.Contains("DataCollectionInstanceOrchestration.OpenDataCollectionInstance", triggered);
        Assert.Contains("DataCollectionInstance.Open", triggered);
        Assert.Contains("MaterialCollection.TrackIn", triggered);
        Assert.Contains("MaterialCollection.Dispatch", triggered);
    }

    [Fact]
    public void BuildEdgeMap_OverrideForExistingOuter_ReplacesDefault()
    {
        var defaults = new List<NestedActionGroupEdge> { new() { Outer = "ComplexFoo", Nested = new() { "OldNested" } } };
        var overrides = new List<NestedActionGroupEdge> { new() { Outer = "ComplexFoo", Nested = new() { "NewNested" } } };

        var map = NestedActionGroupCatalog.BuildEdgeMap(defaults, overrides);

        Assert.Equal(new[] { "NewNested" }, map["ComplexFoo"]);
    }

    [Fact]
    public void BuildEdgeMap_OverrideForNewOuter_IsAdded()
    {
        var defaults = new List<NestedActionGroupEdge> { new() { Outer = "ComplexFoo", Nested = new() { "Foo" } } };
        var overrides = new List<NestedActionGroupEdge> { new() { Outer = "ComplexBar", Nested = new() { "Bar" } } };

        var map = NestedActionGroupCatalog.BuildEdgeMap(defaults, overrides);

        Assert.Equal(new[] { "Foo" }, map["ComplexFoo"]);
        Assert.Equal(new[] { "Bar" }, map["ComplexBar"]);
    }

    [Fact]
    public void BuildEdgeMap_NoOverrides_ReturnsDefaultsOnly()
    {
        var defaults = new List<NestedActionGroupEdge> { new() { Outer = "ComplexFoo", Nested = new() { "Foo" } } };

        var map = NestedActionGroupCatalog.BuildEdgeMap(defaults);

        Assert.Equal(new[] { "Foo" }, map["ComplexFoo"]);
    }

    [Fact]
    public void ResolveTransitiveTriggered_MultiHopChain_FlattensAllReachableNodes()
    {
        // Mirrors the real confirmed case: ComplexTrackOutAndMoveMaterialsToNextStep calls two other
        // orchestration methods, each of which has its own further nested edge.
        var map = new Dictionary<string, List<string>>
        {
            ["ComplexTrackOutAndMoveMaterialsToNextStep"] = new() { "ComplexTrackOutMaterials", "ComplexMoveMaterialsToNextStep" },
            ["ComplexTrackOutMaterials"] = new() { "MaterialCollection.TrackOut" },
            ["ComplexMoveMaterialsToNextStep"] = new() { "MaterialCollection.MoveToNextStep" }
        };

        var result = NestedActionGroupCatalog.ResolveTransitiveTriggered("ComplexTrackOutAndMoveMaterialsToNextStep", map);

        Assert.Equal(
            new[] { "ComplexTrackOutMaterials", "ComplexMoveMaterialsToNextStep", "MaterialCollection.TrackOut", "MaterialCollection.MoveToNextStep" },
            result);
    }

    [Fact]
    public void ResolveTransitiveTriggered_NoEntryForCandidate_ReturnsEmpty()
    {
        var map = new Dictionary<string, List<string>> { ["ComplexFoo"] = new() { "Foo" } };

        var result = NestedActionGroupCatalog.ResolveTransitiveTriggered("SomethingElse", map);

        Assert.Empty(result);
    }

    [Fact]
    public void ResolveTransitiveTriggered_CyclicalEdge_DoesNotInfiniteLoop()
    {
        // Shouldn't happen in the hand-curated table, but a bad project-supplied override could
        // introduce a cycle -- must terminate rather than hang.
        var map = new Dictionary<string, List<string>>
        {
            ["A"] = new() { "B" },
            ["B"] = new() { "A" }
        };

        var result = NestedActionGroupCatalog.ResolveTransitiveTriggered("A", map);

        Assert.Equal(new[] { "B" }, result);
    }
}
