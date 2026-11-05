# Rule Reference

Built-in checks implemented by `CmFrameworkScanner.GetFindings`. When a finding's severity escalates, the
finding's JSON also carries a machine-readable `Escalation` object (base severity, reasons, nested-loop
depth) — see [commands.md](commands.md#scan). Severity can escalate above the base level
listed here for `LoadInsideLoop`, `ResolveInsideLoop`, `QueryExecuteInsideLoop`, `SaveInsideLoop`,
`CreateInstanceInsideLoop`, `DeleteRemoveInsideLoop`, `OrchestrationSingularInsideLoop`, and
`OrchestrationNoBulkInsideLoop` when the call sits in a nested loop (depth ≥ 2)
or the enclosing method is itself transitively called from a loop elsewhere (up to 3 call-graph hops) —
see `CmLoopExposureFinder`. Cross-method caller chains are name-based, with two guards against
fabricated chains between unrelated code: a method name defined by more than one type in the scanned
scope is treated as ambiguous and gets no caller edges at all; and local functions are never callable
targets (a `receiver.Method()` call can't resolve to one), so a platform call like
`_exceptionOrchestration.OpenProtocolInstance(...)` is never misbound to a same-named local helper.

**Single-execution loop bodies don't count as "inside a loop."** These loop-sensitive rules, plus
`ExcessiveLazyLoading`'s loop-body check, are suppressed when the loop provably runs the call at
most once per method call — every path from the call either exits via `return`/`throw`/`break` or
never reaches it again (`CmLoopAnalyzer.GetLoopState`'s `SingleExecutionLines`, a purely syntactic
tail-completion analysis). This covers both a guaranteed exit at the loop body's top level
(`Load(); return x;`) and one inside a nested block (`if (found) { Load(); break; }` — whenever the
load runs, the loop stops). A `continue` after the call, or any path that falls back into the next
iteration, still flags it. A guaranteed `return`/`throw` suppresses at any loop nesting depth (it
unwinds the whole method); a guaranteed `break` only when that loop has no enclosing loop, since a
`break` alone doesn't stop an outer loop from re-entering. Tails inside a `try` body are never
trusted — a `catch` in the same loop can swallow the exception and keep iterating.

**Local functions called from a loop count as "inside a loop" too.** A load-bearing call physically
outside any loop but sitting in a local function that a loop calls still runs once per iteration.
`CmLoopAnalyzer` marks such a local function's body as loop-exposed: it resolves bare-name calls to
their local-function declaration (exact, single-file — no call graph), and if a call site is inside
a loop the whole helper body is treated as loop-membership for the loop-sensitive rules. Exposure
propagates through helper→helper chains and compounds with real loops (a load already in an inner
loop inside a helper called from an outer loop escalates to nested severity). Self-recursion is
excluded and mutual-recursion cycles are broken — recursion is a different pattern with no "batch it"
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
| `LoadReferenceForFkOnly` | Medium | A reference entity's `.Id`/`.DefinitionId` is accessed. Verified against platform source: in server-side (pipeline) code the reference getter builds an Id-only stub and then implicitly calls `Load()` on it, so the dereference costs a full per-instance load while the raw FK already sits in the entity's native attributes | Use `GetNativeValue<long>("PropertyName")` to read the stored FK without triggering the load. Caveat: for versioned references (Product, Flow, ...) the stored value is the *DefinitionId* — if the specific version Id is genuinely needed, the load is legitimate. |
| `ResolveInsideLoop` | High (escalates to Critical) | `.Resolve(` inside a loop body | Hoist the smart table load and prefer `BulkResolve` or a preloaded in-memory lookup. |
| `QueryExecuteInsideLoop` | High (escalates to Critical) | `*query*.Execute(` / `queryObject.Execute(` inside a loop body | Batch the query inputs and execute once, then group results in memory. |
| `SaveInsideLoop` | High (escalates to Critical) | `.SaveAttributes(`/`.Save(` inside a loop body, unless the receiver expression contains "Collection" (the batch-save form) | Batch entities into a collection and perform one collection `Save()` outside the loop — entity-aware, see below. |
| `CreateInstanceInsideLoop` | High (escalates to Critical) | `.CreateInstance(` inside a loop body (does not match the bulk `.CreateInstances(` overload) | Batch the names/attributes and call `CreateInstances` once outside the loop instead of `CreateInstance` per iteration — entity-aware, see below. |
| `DeleteRemoveInsideLoop` | High (escalates to Critical) | `.Delete(`/`.Remove(` inside a loop body, where the receiver is a known entity-type reference (`material.Delete()`, `x.Material.Remove()`) rather than a collection or unrelated type | Batch entities into a collection and perform one collection `Delete()`/`Remove()` outside the loop — entity-aware, see below. |
| `OrchestrationSingularInsideLoop` | High (escalates to Critical) | A per-item Orchestration method with a confirmed bulk sibling on the same interface (`TrackInMaterial`/`TrackInMaterials`, `CreateContainer`/`CreateContainers`, ...) called on an `*orchestration*` receiver inside a loop — see `OrchestrationMethodCatalog` | Batch the inputs and call the named bulk sibling once outside the loop. |
| `OrchestrationNoBulkInsideLoop` | High (escalates to Critical) | An Orchestration method with real per-call cost and NO platform bulk alternative (`EmptyContainer`, `MoveMaterialToStep`, `GradeMaterial`, ...) called on an `*orchestration*` receiver inside a loop | No bulk alternative exists — review whether the loop's iteration count can be reduced (the fix deliberately never points at a method that doesn't exist). |
| `LoopInducedByCaller` | Critical | A load/resolve/query-execute/save/create-instance/delete-remove/orchestration call sits in a method that is not itself in a loop, but is called (directly or transitively, ≤3 hops) from a loop elsewhere | Hoist the call to the caller and pass preloaded data in, or make the method accept a collection so it can batch internally. |
| `LoadDataWithoutFilters` | Critical | `.LoadData()` called with no filter arguments | Use `LoadData(filters)` and constrain the table to the rows needed. |
| `ConfigGetConfig` | Medium | `Config.GetConfig(` called directly | Prefer a project-level config wrapper service's `TryGetConfigValue`, or `Config.TryGetConfig`, depending on the layer. |
| `ConfigWriteInHotPath` | High | A local `IConfig` variable (from a declaration or `Config.TryGetConfig(..., out var)`) has both its `.Value` assigned and `.Save()` called, outside one-time migration DEEs (`.../DEEs/ProcessRules/...`) | Config entries should rarely change; store frequently-changing/counter-like values in an entity attribute, generic table, or cache instead. |
| `ApplicationContextInExtension` | Medium | An extension method file (`Extensions/` folder or `*Extensions.*` filename) references `ApplicationContext` | Pass required services into the caller, or move the logic into an injectable service. |
| `DirectEntityInstantiation` | Medium | `new Material(`, `new Product(`, etc. — direct construction of a known entity type | Use `entityFactory.Create<TInterface>()` instead of direct construction. |
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
`GenerateSchema` and `GenerateEntityTypeDBSchema` after the update. The normal
`IMicronEntityTypeService.UpdateEntityTypeProperties` route does not require a duplicate schema
call. A guarded removal followed by recreation supports deliberate attribute-definition repairs;
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
`LoadReferenceForFkOnly`, and `DirectEntityInstantiation` all key off `EntityCatalog.Names` — the set
of real CM Framework entity type names. This is **not** a hand-maintained list: it's resolved per
repository at scan time by `EntityCatalogResolver`, which reflects (metadata-only, via
`MetadataLoadContext`) over the platform assemblies the repository references — located from each
project's `obj/project.assets.json` — and classifies every entity type by its base class. It therefore
tracks whatever platform version a repository is actually on, and automatically includes that
repository's own custom entities, with no snapshot to keep up to date. (Because it reads referenced
assemblies, the repository must be restored; see [commands.md](commands.md) — "Entity catalog".)
"Batch this into a collection" isn't equally true for every entity, so each is categorized by its base
class into how it actually supports bulk operations:

- **`Standard`** (`EntityInstance`/`EntityVersion`, e.g. `Material`, `Product`) — bulk `Load`/`Save`/
  `Delete`, plus bulk create via a generated `{Entity}.CreateInstances(names)` on the singular class.
  This is what the suggested-fix text has always assumed, and is correct for these.
- **`Relation`** (`EntityRelation`, e.g. `SubContainer`, `WorkgroupResource` — ~20% of all entities)
  — bulk `Load`/`Save`/`Delete` fine, but there is **no `CreateInstances`**; bulk creation normally
  goes through the parent entity's `AddXRelations`-style helper instead.
- **`ChildScoped`** (`ChildEntityInstance`/`StateModelFlowInstance`, e.g. `RecipeInstance`,
  `ProtocolInstance`) — bulk `Load`/`Save`/`Delete` fine, but `Create()` is explicitly blocked;
  creation must go through the parent entity.
- **`NoBulk`** (built on a plain `CoreBase`/`CoreBaseCollection<T>` instead of the entity hierarchy,
  e.g. `SmartTable`, `GenericTable`, `Action`, `Config`, `Role`, `LookupTable`) — no bulk operation of
  any kind exists.

A few small static sets on `EntityCatalog` stay **hand-curated**, because they encode facts reflection
can't see from type metadata:
- `CreateDisabled` (`MaterialDeviation`, `RecipeBody`, `RecipeInstance`, `ScheduleScenarioMessage`,
  `MaterialLineStep`) and `AllBulkDisabled` (`MaterialLineStep` only) — entities that override a bulk
  method to *throw* `InvalidOperationCmfException` even though their category would allow it. That's a
  method-body (IL) fact, invisible to metadata reflection.
- `NonLazyLoadedReferenceNames` (`EntityType`, `EntityTypeProperty`, `GenericTableProperty`,
  `SmartTableProperty`, `ScalarType`) — in-memory structural-metadata definitions. Reflection classifies
  them as entities (they're `CoreBase`-derived, like the *functional* second-class objects), but
  dereferencing them never triggers a lazy load. Note that being `NoBulk` does **not** by itself imply
  "in-memory": functional `CoreBase` entities like `ActionGroup`/`StateModel`/`Action` genuinely lazy-load,
  so this exclusion is a targeted allowlist, not a blanket category rule.
- `IdiomaticDirectInstantiationNames` (`QueryObject`) — entity-shaped types whose direct `new` is idiomatic.

`CmFrameworkScanner.GetSaveFixMessage`/`GetCreateFixMessage`/`GetDeleteFixMessage` resolve the
receiver's entity name (`TryGetKnownEntityName`) and return category-specific suggested-fix text —
or `null` when the entity is `Standard` (or unresolved), in which case `LoopSensitiveCallRule` falls
back to the rule's own fixed text, unchanged from before. This is deliberately conservative: a
receiver name that doesn't match any catalog entry never gets a claim about bulk operations it
wasn't verified to have.

## Adding a new rule

Each rule is a small class implementing `IReviewRule` (`src/Rules/IReviewRule.cs`),
matching against the file's parsed syntax tree — not text/regex — via a `RuleFileContext`. See the doc
comment on `IReviewRule` for the three patterns to follow: a loop-sensitive call pattern reuses
`LoopSensitiveCallRule`, an entity-reference property-access pattern reuses `EntityPropertyAccessRule`
(both handle the null-conditional `?.` form automatically), and anything else is a new small class
alongside `DirectEntityInstantiationRule` in `Rules/`. Then register the new instance in
`CmFrameworkScanner.Rules`.

Syntax-tree walking helpers shared across rules and analyzers (parsing, line numbers, and matching a
member access whether written as `a.Foo` or the null-conditional `a?.Foo`) live in
`src/Analysis/CodeReviewSyntaxTree.cs`.

## Cross-reference and loop-exposure context

Findings inside DEE files carry a `CrossReference` back to the Orchestration call site that triggers them
(`CalledByOrchestration`/`NoCallerFound`), and Orchestration findings that are triggered by a DEE call
carry the reverse link (`CalledByDee`). Findings flagged via `LoopInducedByCaller` (or escalated for the
same reason) carry a `LoopExposure` chain describing the call path back to the loop that causes the
repetition — rendered as prose by `CmLoopExposureFinder.FormatChain` and included in both the Standard and
Detailed reports.
