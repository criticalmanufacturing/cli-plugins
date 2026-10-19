using Cmf.Cli.Plugin.CodeReview.Rules;

namespace codereview.tests;

/// <summary>
/// A hermetic <see cref="EntityCatalog"/> for tests. In production the catalog is resolved by
/// reflection over the repository's referenced assemblies (<see cref="EntityCatalogResolver"/>), but
/// temp-dir test fixtures have no restored assemblies, so tests inject this fixed catalog into
/// <c>GetFindings(..., entityCatalog: TestEntities.Catalog)</c> instead. It covers exactly the entity
/// names the scanner/settings tests exercise, with the categories those tests assert against.
/// </summary>
public static class TestEntities
{
    public static readonly EntityCatalog Catalog = new(new Dictionary<string, EntityBulkCategory>(StringComparer.Ordinal)
    {
        ["Material"] = EntityBulkCategory.Standard,
        ["Product"] = EntityBulkCategory.Standard,
        ["Container"] = EntityBulkCategory.Standard,
        ["Resource"] = EntityBulkCategory.Standard,
        ["Step"] = EntityBulkCategory.Standard,
        ["Flow"] = EntityBulkCategory.Standard,
        // MaterialDeviation is a Standard entity whose CreateInstances throws -- the "bulk create
        // disabled" message comes from the static EntityCatalog.CreateDisabled set, not the category.
        ["MaterialDeviation"] = EntityBulkCategory.Standard,
        // In Names so the DirectEntityInstantiation / ExcessiveLazyLoading rules would match, then get
        // exempted by the static IdiomaticDirectInstantiationNames / NonLazyLoadedReferenceNames sets.
        ["QueryObject"] = EntityBulkCategory.Standard,
        ["EntityType"] = EntityBulkCategory.Standard,
        ["SubMaterial"] = EntityBulkCategory.Relation,
        ["SubContainer"] = EntityBulkCategory.Relation,
        ["SmartTable"] = EntityBulkCategory.NoBulk,
        ["GenericTable"] = EntityBulkCategory.NoBulk,
        ["Action"] = EntityBulkCategory.NoBulk,
    });
}
