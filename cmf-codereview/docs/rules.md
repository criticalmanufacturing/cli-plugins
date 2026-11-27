# Rule Reference

Built-in checks implemented by `CmFrameworkScanner.GetFindings`. When a finding's severity escalates, the
finding's JSON also carries a machine-readable `Escalation` object (base severity, reasons, nested-loop
depth) â€” see [commands.md](commands.md#scan). Severity can escalate above the base level
listed here for `LoadInsideLoop`, `ResolveInsideLoop`, `QueryExecuteInsideLoop`, `SaveInsideLoop`,
`CreateInstanceInsideLoop`, `DeleteRemoveInsideLoop`, `OrchestrationSingularInsideLoop`, and
`OrchestrationNoBulkInsideLoop` when the call sits in a nested loop (depth â‰¥ 2)
or the enclosing method is itself transitively called from a loop elsewhere (up to 3 call-graph hops) â€”
see `CmLoopExposureFinder`. Cross-method caller chains are name-based, with two guards against
fabricated chains between unrelated code: a method name defined by more than one type in the scanned
scope is treated as ambiguous and gets no caller edges at all; and local functions are never callable
targets (a `receiver.Method()` call can't resolve to one), so a platform call like
`_exceptionOrchestration.OpenProtocolInstance(...)` is never misbound to a same-named local helper.

**Single-execution loop bodies don't count as "inside a loop."** These loop-sensitive rules, plus
`ExcessiveLazyLoading`'s loop-body check, are suppressed when the loop provably runs the call at
most once per method call â€” every path from the call either exits via `return`/`throw`/`break` or
never reaches it again (`CmLoopAnalyzer.GetLoopState`'s `SingleExecutionLines`, a purely syntactic
tail-completion analysis). This covers both a guaranteed exit at the loop body's top level
(`Load(); return x;`) and one inside a nested block (`if (found) { Load(); break; }` â€” whenever the
load runs, the loop stops). A `continue` after the call, or any path that falls back into the next
iteration, still flags it. A guaranteed `return`/`throw` suppresses at any loop nesting depth (it
unwinds the whole method); a guaranteed `break` only when that loop has no enclosing loop, since a
`break` alone doesn't stop an outer loop from re-entering. Tails inside a `try` body are never
trusted â€” a `catch` in the same loop can swallow the exception and keep iterating.

**Local functions called from a loop count as "inside a loop" too.** A load-bearing call physically
outside any loop but sitting in a local function that a loop calls still runs once per iteration.
`CmLoopAnalyzer` marks such a local function's body as loop-exposed: it resolves bare-name calls to
their local-function declaration (exact, single-file â€” no call graph), and if a call site is inside
a loop the whole helper body is treated as loop-membership for the loop-sensitive rules. Exposure
propagates through helperâ†’helper chains and compounds with real loops (a load already in an inner
loop inside a helper called from an outer loop escalates to nested severity). Self-recursion is
excluded and mutual-recursion cycles are broken â€” recursion is a different pattern with no "batch it"
fix, so a recursive tree-walker isn't flagged just for calling itself in a `foreach`.

**Loop-invariant receivers don't count either (`ExcessiveLazyLoading`).** CM lazy loading caches on
the entity instance after the first dereference, so the repeated cost only exists when the receiver
is a *different instance* per iteration. `item.Material.X` (iteration variable) or a receiver
reassigned inside the loop flags; `input.Material.X` where `input` never changes inside the loop
loads once and is not flagged (`CmReceiverVariance`).

**Never treated as lazy-loading**: dereferencing `.EntityType` (in-memory metadata present on every
loaded entity). **Never treated as direct entity instantiation**: `new QueryObject()` (the canonical
ad-hoc query pattern). See `EntityCatalog.NonLazyLoadedReferenceNames` /
`IdiomaticDirectInstantiationNames`.

| Rule | Base Severity | Flags | Suggested Fix |
|---|---|---|---|
| `LevelsToLoadGreaterThanOne` | Critical | `.Load(levelsToLoad: N)` / `.LoadRelations(...)` / `.LoadChildren(...)` with `N > 1` | Use the default one-level load and load deeper references only where actually needed. |
| `LoadInsideLoop` | High (escalates to Critical) | `.Load`/`.LoadRelations`/`.LoadAttributes`/`.LoadChildren`/`.LoadReference`/`.LoadReferences` inside a loop body | Batch entities into a collection and perform one collection load outside the loop. |
| `ExcessiveLazyLoading` | High | A known entity-type reference property (see the reflection-resolved entity catalog below) is dereferenced inside a loop or a LINQ lambda (`=>`) | Load the reference in advance with `LoadReference`/`LoadReferences` (or `LoadRelations` for collections) before the loop/LINQ chain. |
| `LoadReferenceForFkOnly` | Medium | A reference entity's `.Id`/`.DefinitionId` is accessed. Verified against platform source: in server-side (pipeline) code the reference getter builds an Id-only stub and then implicitly calls `Load()` on it, so the dereference costs a full per-instance load while the raw FK already sits in the entity's native attributes | Use `GetNativeValue<long>("PropertyName")` to read the stored FK without triggering the load. Caveat: for versioned references (Product, Flow, ...) the stored value is the *DefinitionId* â€” if the specific version Id is genuinely needed, the load is legitimate. |
| `ResolveInsideLoop` | High (escalates to Critical) | `.Resolve(` inside a loop body | Hoist the smart table load and prefer `BulkResolve` or a preloaded in-memory lookup. |
| `QueryExecuteInsideLoop` | High (escalates to Critical) | `*query*.Execute(` / `queryObject.Execute(` inside a loop body | Batch the query inputs and execute once, then group results in memory. |
| `SaveInsideLoop` | High (escalates to Critical) | `.SaveAttributes(`/`.Save(` inside a loop body, unless the receiver expression contains "Collection" (the batch-save form) | Batch entities into a collection and perform one collection `Save()` outside the loop â€” entity-aware, see below. |
| `CreateInstanceInsideLoop` | High (escalates to Critical) | `.CreateInstance(` inside a loop body (does not match the bulk `.CreateInstances(` overload) | Batch the names/attributes and call `CreateInstances` once outside the loop instead of `CreateInstance` per iteration â€” entity-aware, see below. |
| `DeleteRemoveInsideLoop` | High (escalates to Critical) | `.Delete(`/`.Remove(` inside a loop body, where the receiver is a known entity-type reference (`material.Delete()`, `x.Material.Remove()`) rather than a collection or unrelated type | Batch entities into a collection and perform one collection `Delete()`/`Remove()` outside the loop â€” entity-aware, see below. |
| `OrchestrationSingularInsideLoop` | High (escalates to Critical) | A per-item Orchestration method with a confirmed bulk sibling on the same interface (`TrackInMaterial`/`TrackInMaterials`, `CreateContainer`/`CreateContainers`, ...) called on an `*orchestration*` receiver inside a loop â€” see `OrchestrationMethodCatalog` | Batch the inputs and call the named bulk sibling once outside the loop. |
| `OrchestrationNoBulkInsideLoop` | High (escalates to Critical) | An Orchestration method with real per-call cost and NO platform bulk alternative (`EmptyContainer`, `MoveMaterialToStep`, `GradeMaterial`, ...) called on an `*orchestration*` receiver inside a loop | No bulk alternative exists â€” review whether the loop's iteration count can be reduced (the fix deliberately never points at a method that doesn't exist). |
| `LoopInducedByCaller` | Critical | A load/resolve/query-execute/save/create-instance/delete-remove/orchestration call sits in a method that is not itself in a loop, but is called (directly or transitively, â‰¤3 hops) from a loop elsewhere | Hoist the call to the caller and pass preloaded data in, or make the method accept a collection so it can batch internally. |
| `LoadDataWithoutFilters` | Critical | `.LoadData()` called with no filter arguments | Use `LoadData(filters)` and constrain the table to the rows needed. |
| `ConfigGetConfig` | Medium | `Config.GetConfig(` called directly | Prefer a project-level config wrapper service's `TryGetConfigValue`, or `Config.TryGetConfig`, depending on the layer. |
| `ConfigWriteInHotPath` | High | A local `IConfig` variable (from a declaration or `Config.TryGetConfig(..., out var)`) has both its `.Value` assigned and `.Save()` called, outside one-time migration DEEs (`.../DEEs/ProcessRules/...`) | Config entries should rarely change; store frequently-changing/counter-like values in an entity attribute, generic table, or cache instead. |
| `ApplicationContextInExtension` | Medium | An extension method file (`Extensions/` folder or `*Extensions.*` filename) references `ApplicationContext` | Pass required services into the caller, or move the logic into an injectable service. |
| `DirectEntityInstantiation` | Medium | `new Material(`, `new Product(`, etc. â€” direct construction of a known entity type | Use `entityFactory.Create<TInterface>()` instead of direct construction. |
| `GetConfigInsteadOfTryGetConfig` | Medium | A `GetConfig(` call that is not `TryGetConfig` and not `Config.GetConfig` (already covered by that rule) | Confirm whether this should use `TryGetConfig` or an existing wrapper service before keeping the direct call. |

## Process Rule deployment validation

`ProcessRuleValidationRule` implements deterministic Roslyn checks for `DeeDevBase` deployment
classes. The authoring guidelines are development reference material: **the scanner never reads
`process-rule-validation-guidelines.md`**. Identical source, package metadata, tool version, and
review configuration produce identical findings. Changes to checks are C# changes with tests.

Process Rules are discovered from Data-package `EntityTypes`, `ProcessRulesPre`, and
`ProcessRulesPost` content entries, including repositories without a general `DEE` entry.
Standard `DEEs/ProcessRules/` paths also work with explicit roots. Classification distinguishes
versioned Before/After, EntityTypes, EntityTypesAttributes, and recurring unversioned Services.
The older `IsOneTimeMigrationFile` exemptions remain unchanged for compatibility; that flag alone
does not imply a Services rule runs once.

| Diagnostic | Default severity | Detects / remediation |
|---|---|---|
| `ProcessRuleCondition` | High | Condition contains more than an unconditional `return true` / `=> true`. Deployment skips it; move guards into `DeeActionCode`. |
| `ProcessRuleUnguardedMutation` | High; Critical when the same guard exists only in the skipped condition | Attribute creation or typed Action/Rule/Config/EntityType creation, deletion, removal, saving, action-group removal, or termination lacks a recognized guard for its own target. |
| `ProcessRuleUnresolvedGuard` | Medium | Attribute target or a helper-based guard cannot be resolved. Review the helper rather than treating it as proven safe. |
| `ProcessRuleTerminationState` | High | Termination lacks an already-terminated state guard. Check both existence and `UniversalState != Terminated`. |
| `ProcessRuleSchemaRegeneration` | High | Raw property collection creation or `FullUpdateEntityType` property changes lack subsequent schema regeneration for the same entity on the same path. |
| `ProcessRuleAttributeRemoval` | High; Critical when matching cleanup occurs after removal | A resolved property removal has no matching earlier `RemoveAttributes` cleanup. Clear all holders and preserve values where required before removing metadata. |
| `ProcessRuleAttributeRemovalReview` | Medium | Removed properties are supplied through an unresolved collection/helper; verify guards and cleanup. |
| `ProcessRuleTableMigration` | Critical | Smart/Generic Table data access or mutation targets schema created/changed in the same package version's MasterData. Move migration to a post-install step or later independent package. |
| `ProcessRuleTableMigrationReview` | Medium | Table mutation has an unresolved name/package/version; verify deployment dependencies and rerun safety. |
| `ProcessRulePlacement` | High | Unsupported folder, conflicting namespace phase/content type, wrong position relative to MasterData, or attribute changes in a general Before/After folder. |
| `ProcessRuleVersionNamespace` | Medium | Namespace version does not match the dotted folder version's underscore form. |
| `ProcessRuleRecurringMigration` | Medium | Recognizable migration mutations under Services will run on every deployment; confirm intent or move to a versioned folder. |
| `ProcessRuleDependencyOrder` | High | A literal entity-type load precedes its unique same-package, same-version creation rule in manifest/alphabetical file order. Move the creator earlier. |

Guards recognize `HasProperty`, `TryGetEntityTypeProperty`, `ObjectExists`, `TryLoad`, boolean
negation/comparisons, applicable branches, and early returns. Checks on another property or target
do not protect an operation. Reachable local functions are analyzed, including guards at their
call sites; dormant local functions and callbacks are excluded. Raw metadata changes recognize
`GenerateSchema` and `GenerateEntityTypeDBSchema` after the update. The supported
`UpdateEntityTypeProperties` helper pattern is treated as schema-aware and does not require a
duplicate schema call; verify that the project's helper performs regeneration. A guarded removal
followed by recreation supports deliberate attribute-definition repairs;
the removal still needs value cleanup.

Schema context is read once per scan from the same Data package's versioned MasterData. It tracks
new Smart/Generic Tables and changes to property structure. Row data, precedence keys, and
description-only updates are not schema changes. Partial property updates retain earlier field
definitions. Names must resolve from local/constant strings, a table's `Name`, or a named `Load`;
unrelated packages' tables are never correlated. Unchanged files provide context even when
`--changed-files-list` restricts the reported findings.

These are bounded syntax checks, not execution or whole-program proofs. External helpers,
callbacks, indirect SQL/query table dependencies, and arbitrary alias/data flow may require
review. Cleanup recognition does not prove a query included every populated holder. Rename/move
value preservation is a developer decision; verify copies finish before cleanup. Released-version
immutability, database execution twice, complete test coverage, and manual release-note steps
need release/deployment evidence and are not inferred from folder age or missing static findings.
Do not edit shipped rules to resolve historical findings: fix forward, or baseline accepted debt.

All diagnostics use the existing per-rule settings, inline suppressions, baselines, changed-file
filtering, severity gates, and JSON/Markdown/HTML reporting. They do not use loop escalation.


## The entity catalog (`src/Rules/EntityCatalog.cs`)

`SaveInsideLoop`, `CreateInstanceInsideLoop`, `DeleteRemoveInsideLoop`, `ExcessiveLazyLoading`,
`LoadReferenceForFkOnly`, and `DirectEntityInstantiation` all key off `EntityCatalog.Names` â€” the set
of real CM Framework entity type names. This is **not** a hand-maintained list: it's resolved per
repository at scan time by `EntityCatalogResolver`, which reflects (metadata-only, via
`MetadataLoadContext`) over the platform assemblies the repository references â€” located from each
project's `obj/project.assets.json` â€” and classifies every entity type by its base class. It therefore
tracks whatever platform version a repository is actually on, and automatically includes that
repository's own custom entities, with no snapshot to keep up to date. (Because it reads referenced
assemblies, the repository must be restored; see [commands.md](commands.md) â€” "Entity catalog".)
"Batch this into a collection" isn't equally true for every entity, so each is categorized by its base
class into how it actually supports bulk operations:

- **`Standard`** (`EntityInstance`/`EntityVersion`, e.g. `Material`, `Product`) â€” bulk `Load`/`Save`/
  `Delete`, plus bulk create via a generated `{Entity}.CreateInstances(names)` on the singular class.
  This is what the suggested-fix text has always assumed, and is correct for these.
- **`Relation`** (`EntityRelation`, e.g. `SubContainer`, `WorkgroupResource` â€” ~20% of all entities)
  â€” bulk `Load`/`Save`/`Delete` fine, but there is **no `CreateInstances`**; bulk creation normally
  goes through the parent entity's `AddXRelations`-style helper instead.
- **`ChildScoped`** (`ChildEntityInstance`/`StateModelFlowInstance`, e.g. `RecipeInstance`,
  `ProtocolInstance`) â€” bulk `Load`/`Save`/`Delete` fine, but `Create()` is explicitly blocked;
  creation must go through the parent entity.
- **`NoBulk`** (built on a plain `CoreBase`/`CoreBaseCollection<T>` instead of the entity hierarchy,
  e.g. `SmartTable`, `GenericTable`, `Action`, `Config`, `Role`, `LookupTable`) â€” no bulk operation of
  any kind exists.

A few small static sets on `EntityCatalog` stay **hand-curated**, because they encode facts reflection
can't see from type metadata:
- `CreateDisabled` (`MaterialDeviation`, `RecipeBody`, `RecipeInstance`, `ScheduleScenarioMessage`,
  `MaterialLineStep`) and `AllBulkDisabled` (`MaterialLineStep` only) â€” entities that override a bulk
  method to *throw* `InvalidOperationCmfException` even though their category would allow it. That's a
  method-body (IL) fact, invisible to metadata reflection.
- `NonLazyLoadedReferenceNames` (`EntityType`, `EntityTypeProperty`, `GenericTableProperty`,
  `SmartTableProperty`, `ScalarType`) â€” in-memory structural-metadata definitions. Reflection classifies
  them as entities (they're `CoreBase`-derived, like the *functional* second-class objects), but
  dereferencing them never triggers a lazy load. Note that being `NoBulk` does **not** by itself imply
  "in-memory": functional `CoreBase` entities like `ActionGroup`/`StateModel`/`Action` genuinely lazy-load,
  so this exclusion is a targeted allowlist, not a blanket category rule.
- `IdiomaticDirectInstantiationNames` (`QueryObject`) â€” entity-shaped types whose direct `new` is idiomatic.

`CmFrameworkScanner.GetSaveFixMessage`/`GetCreateFixMessage`/`GetDeleteFixMessage` resolve the
receiver's entity name (`TryGetKnownEntityName`) and return category-specific suggested-fix text â€”
or `null` when the entity is `Standard` (or unresolved), in which case `LoopSensitiveCallRule` falls
back to the rule's own fixed text, unchanged from before. This is deliberately conservative: a
receiver name that doesn't match any catalog entry never gets a claim about bulk operations it
wasn't verified to have.

## Adding a new rule

Each rule is a small class implementing `IReviewRule` (`src/Rules/IReviewRule.cs`),
matching against the file's parsed syntax tree â€” not text/regex â€” via a `RuleFileContext`. See the doc
comment on `IReviewRule` for the three patterns to follow: a loop-sensitive call pattern reuses
`LoopSensitiveCallRule`, an entity-reference property-access pattern reuses `EntityPropertyAccessRule`
(both handle the null-conditional `?.` form automatically), and anything else is a new small class
alongside `DirectEntityInstantiationRule` in `Rules/`. Then register the new instance in
`CmFrameworkScanner.Rules`.

Syntax-tree walking helpers shared across rules and analyzers (parsing, line numbers, and matching a
member access whether written as `a.Foo` or the null-conditional `a?.Foo`) live in
`src/Analysis/CodeReviewSyntaxTree.cs`.

## Cross-reference and loop-exposure context

### Action Group Summary

MasterData wiring is many-to-many: one DEE can be wired to several Action Groups, and each group can
have several DEEs. The resolver reads `DEEActionOrder` objects from numeric version folders under
every Data manifest's `MasterData` content prefix. It retains every distinct `(Action, ActionGroup)`
pair, including three or more groups in one file. An exact repeated pair keeps the highest numeric
version's source across **all** roots; equal versions choose the ordinally greatest repository-relative
path (using `/` separators). Missing version components are zero, so `2.10` equals `2.10.0`.

`DeeCount` counts distinct DEE names under **each** group they are wired to, including DEEs with no
findings or no source file in the selected scan scope. Repeated pairs do not increase the count.
`RiskLevel` describes coupling: 1 DEE is `Low`, 2 are `Medium`, 3 are `High`, and 4 or more are
`Critical`. It is separate from finding severity and does not affect `--fail-on`. Pre and Post
groups remain distinct. Summaries sort by descending count, then ordinal Action Group name; DEE
names and possible endpoints sort ordinally.

`PossibleServiceEndpoints` lists possible static callers found by walking the existing name-based
call graph backwards from the group's method segment. This is not runtime observation or proof of
reachability through compiled platform code. It retains the existing endpoint resolver's
`Cmf.Custom.Business/Cmf.Custom.Services` controller discovery limitation. Legacy report fallback
without a scan graph leaves endpoint lists empty.

This summary applies recognized Process Rule removals after JSON additions. Trace identifiers preserve the exact Action Group name.
Malformed/unreadable MasterData JSON is skipped with a warning on stderr; unrelated valid files
still contribute wiring.

Both report modes show the summary in Markdown and HTML. SARIF records it as context under
`runs[0].properties.actionGroupSummaries`, without adding diagnostics. See
[the findings compatibility contract](action-group-wiring.md) for serialization and command options.

### Action Group Origin

`Origin` uses declared names from the repository's full Both scope, including when findings are
restricted to changed files or a narrower scope. A matching Action Group class-name segment gives
`Custom`; otherwise a matching distinctive method-name segment can give `Custom` when the runtime
module name differs from its C# type name. With a declared-type index but no match, the result is
`Oob`. This is source-name evidence, not a semantic proof that a type lives in a platform assembly.
Without a declared-type index, including legacy report fallback, the result remains `Unknown`.
Generated reports explain unavailable context and recommend enriched findings or `review`.

**Constants-folder exclusion.** A metadata holder under `Common/Constants` can share a type or helper
method name with a real platform implementation. Those declarations describe APIs rather than
implementing them; counting them as implementation evidence would incorrectly imply `Custom`.

An exact, case-sensitive `Constants` directory segment is excluded from **both** declared type and
method indexes before syntax traversal. Windows, Unix and mixed separators are supported, including
a relative path starting with `Constants/`. `Constants.cs`, `MyConstants` and `ConstantsHelpers` do
not match. A genuine same-named implementation elsewhere still contributes evidence. This exclusion
applies only to Origin evidence; normal rules still analyze Constants files.

`CollectDeclaredNames` collects both sets in one cached traversal per eligible file. The public
`CollectDeclaredTypeNames` and `CollectDeclaredMethodNames` wrappers remain available, while the
scanner calls the combined collector once. [Origin classification](origin-classification.md) explains
the evidence and how to retain it in reports.

**Excluding generic BusinessObject lifecycle verbs.** The method-fallback guard excludes
`Create`, `CreateEntity`, `CreateVersion`, `Save`, `Terminate`, `Load`, `TryLoad`,
`LoadRelations`, `RemoveRelations`, `AddRelations`, `AddRelatedRelations`, `RemoveRelatedRelations`,
`ObjectExists`, `GetVersions`, `Delete`, `Clone`, `ToString`, `Equals`, `GetHashCode` and `GetType`.
A coincidental `Create()` method on an unrelated factory is insufficient evidence for custom
ownership. The guard restricts method-name fallback only: a genuine declared type remains `Custom`
even when its method is a lifecycle verb.

Findings inside DEE files carry a `CrossReference` back to the Orchestration call site that triggers them
(`CalledByOrchestration`/`NoCallerFound`), and Orchestration findings that are triggered by a DEE call
carry the reverse link (`CalledByDee`). Findings flagged via `LoopInducedByCaller` (or escalated for the
same reason) carry a `LoopExposure` chain describing the call path back to the loop that causes the
repetition â€” rendered as prose by `CmLoopExposureFinder.FormatChain` and included in both the Standard and
Detailed reports.

### Recognized wiring removals

Versioned Process Rule scripts can remove all groups with `RemoveAction` and
`IsToRemoveAllVersions = true`, or `RemoveActionGroups(action.ActionGroupActions)`.
A collection filtered with `ActionGroup.Name.Equals("Group")` removes one group.
Names must resolve to literals or literal local variables, with exactly one distinct DEE name
in the script. Ambiguous, helper-generated or dynamic names are outside this bounded analysis.
Removal wins regardless of version order, including a subsequent JSON re-add; deployment history
is not replayed. Ordinary DEEs outside ProcessRules are not removal sources.

### Per-finding Action Groups

DEE findings carry `ActionGroups` and `ActionGroupSources` lists. Corresponding indexes identify
each group and its selected MasterData source. Markdown and HTML render all groups comma-joined;
SARIF stores the lists in each result's properties. Unwired and non-DEE findings have null lists.
The default findings file remains an array, with additive fields; consumers should tolerate new fields.

### Also Triggers: curated nested Action Groups

The embedded catalog contains 75 outer keys covering MaterialManagement, EdcManagement, LaborManagement,
ResourceManagement, WarehouseManagement, ExceptionManagement, MappingManagement and BusinessObject chains.
`TriggeredActionGroups` uses breadth-first traversal with a visited set, preserving direct-edge order and
terminating cycles. It describes known candidate method names rather than runtime-observed executions.
Prefixed orchestration names have explicit bridge entries so multi-hop data-collection chains reach
their BusinessObject terminal. Markdown and HTML show Also Triggers; enriched JSON and SARIF retain it.
An absent catalog entry means no known edge, not proof of no nested activity.

| Outer method | Confirmed direct nested methods |
|---|---|
| `ComplexTrackOutMaterials` | `TrackOutMaterials`, `DataCollectionInstanceOrchestration.PerformImmediateDataCollection` |
| `TrackOutMaterials` | `MaterialCollection.TrackOut` |
| `MaterialCollection.TrackOut` | `MaterialCollection.MoveToNextStep` |
| `ComplexTrackInMaterials` | `TrackInMaterials`, `DataCollectionInstanceOrchestration.PerformImmediateDataCollection`, `DataCollectionInstanceOrchestration.OpenDataCollectionInstance` |
| `TrackInMaterials` | `MaterialCollection.TrackIn` |
| `MaterialCollection.TrackIn` | `MaterialCollection.MoveToStep` |
| `ComplexMoveMaterialsToNextStep` | `MoveMaterialsToNextStep` |
| `MoveMaterialsToNextStep` | `MaterialCollection.MoveToNextStep` |
| `ComplexMoveMaterialToNextStep` | `ComplexMoveMaterialsToNextStep` |
| `MoveMaterialToNextStep` | `MoveMaterialsToNextStep` |
| `ComplexMoveMaterialToStep` | `MoveMaterialToStep` |
| `MoveMaterialToStep` | `Material.MoveToStep` |
| `Material.MoveToStep` | `MaterialCollection.MoveToStep` |
| `ComplexTrackInMaterial` | `ComplexTrackInMaterials` |
| `ComplexTrackOutMaterial` | `ComplexTrackOutMaterials` |
| `ComplexTrackOutAndMoveMaterialToNextStep` | `ComplexTrackOutMaterial`, `MoveMaterialToNextStep` |
| `ComplexTrackOutAndMoveMaterialsToNextStep` | `ComplexTrackOutMaterials`, `ComplexMoveMaterialsToNextStep` |
| `ComplexDispatchAndTrackInMaterial` | `DispatchMaterial`, `ComplexTrackInMaterial` |
| `ComplexDispatchAndTrackInMaterials` | `DispatchMaterials`, `ComplexTrackInMaterials` |
| `DispatchMaterial` | `DispatchMaterials` |
| `DispatchMaterials` | `MaterialCollection.Dispatch` |
| `ComplexReworkMaterial` | `ComplexReworkMaterials` |
| `ComplexReworkMaterials` | `ReworkMaterials` |
| `ReworkMaterials` | `MaterialCollection.Rework` |
| `MaterialCollection.Rework` | `Material.RemoveFromLine` |
| `AbortMaterialProcess` | `AbortMaterialsProcess` |
| `AbortMaterialsProcess` | `Materials.AbortProcess` |
| `PerformMaterialProcess` | `PerformMaterialChecklistItems`, `DataCollectionInstanceOrchestration.PostDataCollectionPoints` |
| `PerformMaterialChecklistItems` | `Material.PerformChecklistItemInstance` |
| `ProcessMaterial` | `DataCollectionInstanceOrchestration.PerformImmediateDataCollection` |
| `RemoteAndLocalReceiveMaterials` | `ReceiveMaterials`, `RemoteReceiveMaterials` |
| `ReceiveMaterials` | `Materials.Receive` |
| `RemoteReceiveMaterials` | `Materials.RemoteReceive` |
| `RequestRemoteAndLocalUnshipMaterials` | `RequestRemoteUnshipMaterials`, `UnshipMaterials` |
| `RequestRemoteUnshipMaterials` | `Materials.RequestRemoteUnship` |
| `UnshipMaterials` | `Materials.Unship` |
| `DetachAllMaterials` | `DetachMaterials` |
| `DetachMaterials` | `Material.RemoveSubMaterials` |
| `HoldMaterial` | `HoldMaterials` |
| `HoldMaterials` | `Materials.Hold` |
| `TerminateMaterial` | `TerminateMaterials` |
| `TerminateMaterials` | `Materials.Terminate` |
| `SetOrUnSetMaterialsDispatchable` | `SetOrUnSetMaterialDispatchable` |
| `SetOrUnSetMaterialDispatchable` | `Material.SetDispatchableFlag` |
| `LinkMaterialWithMap` | `LinkMaterialWithMaps` |
| `LinkMaterialWithMaps` | `Material.LinkMaterialWithMaps` |
| `ComplexPerformDataCollection` | `PerformImmediateDataCollection` |
| `PerformImmediateDataCollection` | `DataCollectionInstance.PerformImmediate` |
| `DataCollectionInstanceOrchestration.PerformImmediateDataCollection` | `DataCollectionInstance.PerformImmediate` |
| `DataCollectionInstanceOrchestration.PostDataCollectionPoints` | `DataCollectionInstance.Post` |
| `DataCollectionInstanceOrchestration.OpenDataCollectionInstance` | `DataCollectionInstance.Open` |
| `ManageResourceEmployees` | `SpecialCheckOutEmployees`, `SpecialCheckInEmployees` |
| `SpecialCheckOutEmployees` | `EmployeeCollection.SpecialCheckOut` |
| `SpecialCheckInEmployees` | `EmployeeCollection.SpecialCheckIn` |
| `CheckOutTrainees` | `SpecialCheckOutEmployees`, `MaintenanceActivityOrderSpecialCheckOutEmployees` |
| `MaintenanceActivityOrderSpecialCheckOutEmployees` | `EmployeeCollection.MaoSpecialCheckOut` |
| `ComplexLogResourceEvent` | `LogResourceEvent`, `DataCollectionInstanceOrchestration.PerformImmediateDataCollection` |
| `LogResourceEvent` | `Resource.LogEvent` |
| `Resource.LogEvent` | `MaintenanceActivityOrderCollection.UpdateCounters` |
| `PerformSetup` | `DataCollectionInstanceOrchestration.PerformImmediateDataCollection` |
| `FullUpdateProtocol` | `StateModelOrchestration.FullUpdateStateModel` |
| `StateModelOrchestration.FullUpdateStateModel` | `StateModel.Save` |
| `ApproveMaterialTransfer` | `ApproveMaterialTransfers` |
| `ApproveMaterialTransfers` | `MaterialTransferCollection.Approve` |
| `MaterialTransferCollection.Approve` | `MaterialTransfer.Send` |
| `CancelMaterialTransfer` | `CancelMaterialTransfers` |
| `CancelMaterialTransfers` | `MaterialTransferCollection.Cancel` |
| `MaterialTransferCollection.Cancel` | `MaterialTransferCollection.InternalTerminate` |
| `MaterialTransferCollection.Reject` | `MaterialTransferCollection.InternalTerminate` |
| `MaterialTransferCollection.InternalTerminate` | `MaterialTransferMaterialCollection.InternalTerminate`, `MaterialCollection.Terminate` |
| `ReceiveMaterialTransfer` | `ReceiveMaterialTransfers` |
| `ReceiveMaterialTransfers` | `MaterialTransfer.Receive` |
| `MaterialTransfer.Receive` | `Material.Receive`, `Material.Unship` |
| `RejectMaterialTransfer` | `RejectMaterialTransfers` |
| `RejectMaterialTransfers` | `MaterialTransferCollection.Reject` |

### Runtime trace identifiers

`ActionGroupSummary.DeeSpanName` is exactly `DEE::{ActionGroup}`, including its complete Pre/Post
suffix. Pre and Post remain distinct: a transaction spans the Pre DEEs, the API operation and the
Post DEEs, so either stage can extend its duration. Use the exact identifier for external correlation;
static catalog or endpoint reachability alone does not prove that a span occurred at runtime.

MasterData files are parsed independently across available processors, then merged in the globally
ordered version/path sequence. Parallel parsing preserves pair selection, stable findings and source
locations, including across multiple MasterData roots.

`ProcessRuleSchemaRegeneration` also checks `IEntityType.AddProperties(...)`. The same entity requires
a recognized subsequent `GenerateSchema()` or `GenerateEntityTypeDBSchema(...)` on the update
execution path. An earlier schema call, a different entity or a conditional schema call that does not
cover the update cannot satisfy it. An existence guard returning before the update is supported.

## DEE references and deployment versions

The reference checks and PR deployment-folder diagnostics are described in
[pr-validation.md](pr-validation.md), including their default severities, required metadata,
historical-folder exclusions, and CLI/pipeline inputs.

ExcessiveLazyLoading recognizes a same-block, unconditional collection `LoadReference`, literal-array
`LoadReferences` or `LoadRelations` before foreach. The same reference and collection must match;
LoadRelations covers any reference. Filtering/ordering chains such as `collection.Where(...)` retain
identity. Reassignment, aliasing, intervening calls/mutations, conditional preloads, local-function or
lambda boundaries and unrecognized collection transformations retain the finding. The check does not
follow values between methods or infer runtime state.

ExcessiveLazyLoading suppresses an access only when its receiver type is resolved, its known base and
interface hierarchy has no EntityBase/IEntityBase, and it exposes no LoadReference/LoadReferences/
LoadRelations members. This covers DTOs with entity-shaped property names. Missing or ambiguous
project/type metadata and unresolved base types retain the previous heuristic finding. Real entities
and types exposing lazy-load APIs keep detection and the preload-before-loop check. The compilation
is shared with DEE reference analysis and does not execute analyzed code.

### NativeValueInsteadOfProperty (Medium)

For resolved platform Material, Product and Resource types (including their standard interfaces),
`GetNativeValue<string>("Name")` can use `.Name`: the verified inherited EntityBase getter is a
scalar native-value wrapper. Source-defined getter overrides must also prove that exact passthrough.
Other attributes, unknown types, differing value types and relation/FK reads are excluded. Missing
semantic context does not produce speculative guidance. The catalog is deliberately limited to these
verified type/property combinations; it does not recommend typed navigation for foreign-key reads.
