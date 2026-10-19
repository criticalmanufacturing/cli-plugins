namespace Cmf.Cli.Plugin.CodeReview.Rules;

/// <summary>
/// How an entity's collection supports bulk operations, driven by which base class it extends in
/// the CM Framework platform (Cmf.Foundation/Cmf.Navigo). Determines what a rule's suggested-fix
/// text can safely claim -- "batch this into a collection" is only true for <see cref="Standard"/>.
/// </summary>
public enum EntityBulkCategory
{
    /// <summary>EntityInstance/EntityVersion. Bulk Load/Save/Delete from EntityBaseCollection&lt;T&gt;;
    /// bulk create via a generated {Entity}.CreateInstances(names) on the singular class.</summary>
    Standard,

    /// <summary>EntityRelation (junction/link entities). Bulk Load/Save/Delete fine, but there is no
    /// CreateInstances -- bulk creation is normally reached through a parent-side AddXRelations-style
    /// helper, not by instantiating the relation directly.</summary>
    Relation,

    /// <summary>ChildEntityInstance/StateModelFlowInstance (parent-scoped child entities). Bulk
    /// Load/Save/Delete fine, but Create() is explicitly blocked -- creation must go through the
    /// parent entity.</summary>
    ChildScoped,

    /// <summary>Built on a plain CoreBase/CoreBaseCollection&lt;T&gt; instead of the entity hierarchy
    /// (e.g. SmartTable, GenericTable, Action, Config). No bulk operation of any kind exists.</summary>
    NoBulk
}

/// <summary>
/// The entity types recognized by the entity-aware rules (direct instantiation, lazy-load property
/// access, FK-only dereference, and the loop-sensitive Save/CreateInstance/Delete rules' suggested-fix
/// text), each mapped to its bulk-operation category (<see cref="EntityBulkCategory"/>).
///
/// This is an INSTANCE, resolved per repository by <see cref="EntityCatalogResolver"/> from the
/// platform (and custom) assemblies that repository actually references -- not a hand-maintained
/// snapshot. See that class for how <see cref="Categories"/> is derived by reflection.
///
/// The small static sets below stay hand-curated because they encode facts reflection can't see from
/// type metadata: <see cref="CreateDisabled"/>/<see cref="AllBulkDisabled"/> are entities whose bulk
/// methods are present but throw <c>InvalidOperationCmfException</c> at runtime (a method-body/IL
/// fact), and <see cref="NonLazyLoadedReferenceNames"/>/<see cref="IdiomaticDirectInstantiationNames"/>
/// are rule-idiom judgments, not platform structure.
/// </summary>
public sealed class EntityCatalog
{
    /// <summary>Entities whose CreateInstances is overridden to throw InvalidOperationCmfException even though their base category would otherwise allow bulk create.</summary>
    public static readonly HashSet<string> CreateDisabled = new(StringComparer.Ordinal)
        { "MaterialDeviation", "RecipeBody", "RecipeInstance", "ScheduleScenarioMessage", "MaterialLineStep" };

    /// <summary>Entities whose Save/Delete (not just Create) are also overridden to throw -- MaterialLineStep is a read-only projection.</summary>
    public static readonly HashSet<string> AllBulkDisabled = new(StringComparer.Ordinal) { "MaterialLineStep" };

    /// <summary>
    /// Reference types whose getter does NOT trigger a lazy load, so dereferencing them isn't an
    /// ExcessiveLazyLoading/LoadReferenceForFkOnly concern. These are in-memory structural-metadata
    /// definitions -- reflection classifies them as entities (they're CoreBase-derived, like the
    /// functional second-class objects), but it can't tell "in-memory metadata" from "lazy-loaded
    /// reference" since that isn't in the type metadata, so this stays hand-curated.
    /// </summary>
    public static readonly HashSet<string> NonLazyLoadedReferenceNames = new(StringComparer.Ordinal)
        { "EntityType", "EntityTypeProperty", "GenericTableProperty", "SmartTableProperty", "ScalarType" };

    /// <summary>Entity-shaped types whose direct construction is idiomatic, not a DirectEntityInstantiation smell.</summary>
    public static readonly HashSet<string> IdiomaticDirectInstantiationNames = new(StringComparer.Ordinal) { "QueryObject" };

    /// <summary>An empty catalog -- used when no entity assemblies could be resolved (entity-aware rules degrade to not firing / generic fix text).</summary>
    public static readonly EntityCatalog Empty = new(new Dictionary<string, EntityBulkCategory>(StringComparer.Ordinal));

    public IReadOnlyDictionary<string, EntityBulkCategory> Categories { get; }
    public IReadOnlySet<string> Names { get; }

    public EntityCatalog(IReadOnlyDictionary<string, EntityBulkCategory> categories)
    {
        Categories = categories;
        Names = categories.Keys.ToHashSet(StringComparer.Ordinal);
    }
}
