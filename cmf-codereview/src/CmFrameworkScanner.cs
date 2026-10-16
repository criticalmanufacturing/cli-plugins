using System.Collections.Concurrent;
using Cmf.Cli.Plugin.CodeReview.Models;
using Cmf.Cli.Plugin.CodeReview.Enums;
using Cmf.Cli.Plugin.CodeReview.Rules;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cmf.Cli.Plugin.CodeReview;

/// <summary>
/// The main rule engine: runs every registered <see cref="IReviewRule"/> against each scanned
/// file's syntax tree, and enriches each match with DEE/Orchestration cross-reference and
/// transitive loop-exposure context to build the final <see cref="Finding"/> list.
///
/// To add a rule, see the "how to add a rule" doc comment on <see cref="IReviewRule"/> and add an
/// instance to <see cref="Rules"/> below -- nothing else in this file needs to change.
/// </summary>
public static class CmFrameworkScanner
{
    private static readonly HashSet<string> LoadStyleMethodNames = new(StringComparer.Ordinal)
    {
        "Load", "LoadRelations", "LoadAttributes", "LoadChildren", "LoadReference", "LoadReferences"
    };

    private static readonly HashSet<string> SaveStyleMethodNames = new(StringComparer.Ordinal) { "SaveAttributes", "Save" };

    private static readonly HashSet<string> DeleteStyleMethodNames = new(StringComparer.Ordinal) { "Delete", "Remove" };

    private static readonly HashSet<string> FkPropertyNames = new(StringComparer.Ordinal) { "Id", "DefinitionId" };

    /// <summary>
    /// True when a Delete/Remove receiver looks like a single known-entity reference (<c>material.Delete()</c> or
    /// <c>x.Material.Remove()</c>) rather than a collection or an unrelated type (<c>materialCollection.Delete()</c>,
    /// <c>list.Remove(x)</c>). Exact-name matching is deliberate: it's the same syntax-only heuristic
    /// <see cref="DirectEntityInstantiationRule"/>/<see cref="EntityPropertyAccessRule"/> already rely on, and it
    /// naturally excludes collection-typed receivers since their identifiers don't match an entity name exactly.
    /// </summary>
    private static bool IsKnownEntityReceiver(ExpressionSyntax? target, EntityCatalog catalog) => TryGetKnownEntityName(target, catalog) is not null;

    /// <summary>The known entity name a Save/CreateInstance/Delete receiver resolves to (<c>material.Save()</c> or <c>x.Material.Save()</c>), or null if it doesn't match any catalog entry.</summary>
    private static string? TryGetKnownEntityName(ExpressionSyntax? target, EntityCatalog catalog)
    {
        if (target is null)
        {
            return null;
        }

        if (CodeReviewSyntaxTree.GetMemberName(target) is { } memberName && catalog.Names.Contains(memberName.Identifier.Text))
        {
            return memberName.Identifier.Text;
        }

        return target is IdentifierNameSyntax identifier
            ? catalog.Names.FirstOrDefault(n => string.Equals(identifier.Identifier.Text, n, StringComparison.OrdinalIgnoreCase))
            : null;
    }

    /// <summary>
    /// Entity-specific override for the Create suggested-fix text, or null when nothing special
    /// applies (falls back to the rule's own fixed insideLoop/inducedByCaller text -- e.g. a
    /// Standard-category entity, or a receiver we couldn't resolve to any catalog entry at all).
    /// </summary>
    private static string? GetCreateFixMessage(string? entityName, EntityCatalog catalog)
    {
        if (entityName is null || !catalog.Categories.TryGetValue(entityName, out var category))
        {
            return null;
        }

        if (EntityCatalog.CreateDisabled.Contains(entityName))
        {
            return $"Bulk create is explicitly disabled for {entityName} (CreateInstances throws InvalidOperationCmfException) -- there is no bulk-create path for this entity; consider whether this call is necessary at all.";
        }

        return category switch
        {
            EntityBulkCategory.Relation => $"{entityName} is a relation entity; there is no CreateInstances -- hoist this into the parent entity's AddXRelations-style bulk call instead.",
            EntityBulkCategory.ChildScoped => $"{entityName} is a child-scoped entity; it can't be bulk-created standalone -- batch this through the parent entity instead.",
            EntityBulkCategory.NoBulk => $"No bulk-create API exists for {entityName} -- consider reducing how often this runs rather than trying to batch it.",
            _ => null
        };
    }

    private static string? GetSaveFixMessage(string? entityName, EntityCatalog catalog) => GetBulkFixMessage(entityName, "Save", catalog);
    private static string? GetDeleteFixMessage(string? entityName, EntityCatalog catalog) => GetBulkFixMessage(entityName, "Delete", catalog);

    /// <summary>
    /// Shared override logic for Save/Delete, which (unlike Create) work generically for
    /// Relation/ChildScoped -- only AllBulkDisabled and NoBulk need different text; everything else
    /// returns null so the rule's own fixed text is used.
    /// </summary>
    private static string? GetBulkFixMessage(string? entityName, string operationName, EntityCatalog catalog)
    {
        if (entityName is null || !catalog.Categories.TryGetValue(entityName, out var category))
        {
            return null;
        }

        if (EntityCatalog.AllBulkDisabled.Contains(entityName))
        {
            return $"Bulk {operationName} is explicitly disabled for {entityName} (throws InvalidOperationCmfException/InvalidOperationForObjectCmfException) -- there is no bulk path for this entity; consider whether this call is necessary at all.";
        }

        return category == EntityBulkCategory.NoBulk
            ? $"No bulk API exists for {entityName} -- consider reducing how often this runs rather than trying to batch it."
            : null;
    }

    private static readonly IReadOnlyList<IReviewRule> Rules = BuildDefaultRules();

    private static IReadOnlyList<IReviewRule> BuildDefaultRules() =>
    [
        new LoopSensitiveCallRule(
            "LoadInsideLoop",
            (invocation, _) => CodeReviewSyntaxTree.GetMemberName(invocation.Expression) is { } name &&
                LoadStyleMethodNames.Contains(name.Identifier.Text),
            "Load-style call",
            "Batch entities into a collection and perform one collection load outside the loop.",
            "Either hoist this load to the caller and pass a preloaded collection into this method, or have this method accept a collection of inputs so it can batch the load itself."),
        new LoopSensitiveCallRule(
            "ResolveInsideLoop",
            (invocation, _) => CodeReviewSyntaxTree.GetMemberName(invocation.Expression)?.Identifier.Text == "Resolve",
            "Smart table resolve",
            "Hoist the smart table load and prefer BulkResolve or one preloaded in-memory lookup.",
            "Either hoist this resolve to the caller and pass a preloaded result into this method, or have this method accept a collection of inputs so it can use BulkResolve."),
        new LoopSensitiveCallRule(
            "QueryExecuteInsideLoop",
            (invocation, _) => CodeReviewSyntaxTree.GetMemberName(invocation.Expression)?.Identifier.Text == "Execute" &&
                (CodeReviewSyntaxTree.GetMemberTarget(invocation.Expression)?.ToString().Contains("query", StringComparison.OrdinalIgnoreCase) ?? false),
            "Query execution",
            "Batch the query inputs and execute once, then group the results in memory.",
            "Either hoist this query to the caller and pass the results into this method, or have this method accept a collection of inputs so it can batch the query."),
        new LoopSensitiveCallRule(
            "SaveInsideLoop",
            (invocation, _) => CodeReviewSyntaxTree.GetMemberName(invocation.Expression) is { } name &&
                SaveStyleMethodNames.Contains(name.Identifier.Text) &&
                !(CodeReviewSyntaxTree.GetMemberTarget(invocation.Expression)?.ToString().Contains("Collection", StringComparison.OrdinalIgnoreCase) ?? false),
            "Save-style call",
            "Batch entities into a collection and perform one collection Save() outside the loop.",
            "Either hoist this save to the caller and pass a preloaded collection into this method, or have this method accept a collection of inputs so it can batch the save itself.",
            fixOverride: (invocation, context) => GetSaveFixMessage(TryGetKnownEntityName(CodeReviewSyntaxTree.GetMemberTarget(invocation.Expression), context.EntityCatalog), context.EntityCatalog)),
        new LoopSensitiveCallRule(
            "CreateInstanceInsideLoop",
            (invocation, _) => CodeReviewSyntaxTree.GetMemberName(invocation.Expression)?.Identifier.Text == "CreateInstance",
            "Entity construction",
            "Batch the names/attributes and call CreateInstances once outside the loop instead of CreateInstance per iteration.",
            "Either hoist this construction to the caller and pass preloaded instances into this method, or have this method accept a collection of inputs so it can use CreateInstances.",
            fixOverride: (invocation, context) => GetCreateFixMessage(TryGetKnownEntityName(CodeReviewSyntaxTree.GetMemberTarget(invocation.Expression), context.EntityCatalog), context.EntityCatalog)),
        new LoopSensitiveCallRule(
            "DeleteRemoveInsideLoop",
            (invocation, context) => CodeReviewSyntaxTree.GetMemberName(invocation.Expression) is { } deleteName &&
                DeleteStyleMethodNames.Contains(deleteName.Identifier.Text) &&
                IsKnownEntityReceiver(CodeReviewSyntaxTree.GetMemberTarget(invocation.Expression), context.EntityCatalog),
            "Delete/Remove call",
            "Batch entities into a collection and perform one collection Delete()/Remove() outside the loop.",
            "Either hoist this delete/remove to the caller and pass a preloaded collection into this method, or have this method accept a collection of inputs so it can batch the delete/remove itself.",
            fixOverride: (invocation, context) => GetDeleteFixMessage(TryGetKnownEntityName(CodeReviewSyntaxTree.GetMemberTarget(invocation.Expression), context.EntityCatalog), context.EntityCatalog)),
        new LoopSensitiveCallRule(
            "OrchestrationSingularInsideLoop",
            (invocation, _) => CodeReviewSyntaxTree.GetMemberName(invocation.Expression) is { } orchName &&
                OrchestrationMethodCatalog.BulkSiblingBySingular.ContainsKey(orchName.Identifier.Text) &&
                OrchestrationMethodCatalog.IsOrchestrationReceiver(CodeReviewSyntaxTree.GetMemberTarget(invocation.Expression)?.ToString()),
            "Per-item Orchestration call",
            "Batch the inputs and make one bulk Orchestration call outside the loop.",
            "Either hoist this Orchestration call to the caller, or have this method accept a collection of inputs so it can make one bulk Orchestration call.",
            fixOverride: (invocation, _) =>
            {
                var singular = CodeReviewSyntaxTree.GetMemberName(invocation.Expression)!.Identifier.Text;
                return $"Batch the inputs and call {OrchestrationMethodCatalog.BulkSiblingBySingular[singular]} once outside the loop instead of {singular} per iteration -- the bulk sibling exists on the same orchestration interface.";
            }),
        new LoopSensitiveCallRule(
            "OrchestrationNoBulkInsideLoop",
            (invocation, _) => CodeReviewSyntaxTree.GetMemberName(invocation.Expression) is { } orchName &&
                OrchestrationMethodCatalog.NoBulkMethodNames.Contains(orchName.Identifier.Text) &&
                OrchestrationMethodCatalog.IsOrchestrationReceiver(CodeReviewSyntaxTree.GetMemberTarget(invocation.Expression)?.ToString()),
            "Orchestration call with real per-call cost",
            "No bulk alternative exists for this Orchestration method at the platform level -- review whether the loop's iteration count can be reduced, or whether the operation is needed for every element.",
            "No bulk alternative exists for this Orchestration method at the platform level -- review whether the caller's loop can be narrowed so this method runs for fewer elements."),
        new LevelsToLoadGreaterThanOneRule(),
        new LoadDataWithoutFiltersRule(),
        new DirectEntityInstantiationRule(),
        new EntityPropertyAccessRule(
            "LoadReferenceForFkOnly",
            FkPropertyNames.Contains,
            Severity.Medium,
            // Verified against platform source: in server-side (pipeline) code the reference getter
            // materializes an Id-only stub and then implicitly calls Load() on it -- so dereferencing
            // the reference at all costs a full per-instance load, while the raw FK value already
            // sits in the entity's native attributes.
            "Reference entity is dereferenced just to read its Id/DefinitionId; in server-side code the property getter implicitly performs a full Load() of the referenced entity to serve this.",
            "Use GetNativeValue<long>(\"PropertyName\") to read the stored foreign key without triggering the load. Caveat: for versioned references (Product, Flow, ...) the stored value is the DefinitionId -- if the specific version Id is genuinely needed, the load is legitimate."),
        new EntityPropertyAccessRule(
            "ExcessiveLazyLoading",
            _ => true,
            Severity.High,
            "Reference-type property is dereferenced inside a loop or LINQ chain, which can trigger one lazy-load database call per instance.",
            "Load the reference in advance with LoadReference/LoadReferences (or LoadRelations for collections) before the loop/LINQ chain instead of relying on lazy loading per instance.",
            extraCondition: (context, access, line) =>
                ((context.LoopState.Lines.Contains(line) && !context.LoopState.SingleExecutionLines.Contains(line)) ||
                 access.Ancestors().Any(a => a is LambdaExpressionSyntax or ArrowExpressionClauseSyntax)) &&
                CmReceiverVariance.ReceiverVariesPerIteration(access)),
        new ConfigGetConfigRule(),
        new GetConfigInsteadOfTryGetConfigRule(),
        new ConfigWriteInHotPathRule(),
        new ApplicationContextInExtensionRule()
    ];

    /// <summary>Rule names for which severity can be escalated when the match sits in a nested loop or a caller-induced loop.</summary>
    public static IReadOnlyCollection<string> GetEscalatableRules() =>
        Rules.SelectMany(r => r.EscalatableRuleNames).ToHashSet(StringComparer.Ordinal);

    /// <summary>True if any finding's severity is at or above <paramref name="threshold"/>. Useful for CI gates that should only fail on severe-enough findings.</summary>
    public static bool AnyAtOrAboveSeverity(IEnumerable<Finding> findings, Severity threshold) =>
        findings.Any(f => f.Severity >= threshold);

    private static string NormalizePathForComparison(string path) => path.Trim().Replace('\\', '/').TrimStart('/');

    /// <summary>
    /// Runs every registered rule over the given repository/roots and returns every finding, sorted by severity then file/line.
    /// When <paramref name="changedFiles"/> is given (relative or absolute paths, e.g. from a git diff), cross-reference and
    /// loop-exposure context is still built from the whole repository, but only findings on those files are returned —
    /// useful for CI gates that should only report on files actually touched by a change.
    /// Per-project configuration (<paramref name="settings"/>, or <c>.codereview.json</c> at the repository root when null)
    /// can disable rules, override severities, and add declarative custom rules; findings matching the repository's
    /// <c>.codereview.baseline.json</c> (when present and <paramref name="applyBaseline"/> is true) are excluded.
    /// </summary>
    public static List<Finding> GetFindings(string repositoryRoot, IEnumerable<string>? roots = null, IEnumerable<string>? changedFiles = null,
        CodeReviewSettings? settings = null, bool applyBaseline = true, EntityCatalog? entityCatalog = null)
    {
        roots ??= CodeReviewPaths.ResolveRoots(repositoryRoot, ReviewScope.Both);
        settings ??= CodeReviewSettings.Load(repositoryRoot);
        var baseline = applyBaseline ? CodeReviewBaseline.Load(repositoryRoot) : null;
        var customRules = settings?.BuildCustomRules();
        var activeRules = customRules is { Count: > 0 } ? Rules.Concat(customRules).ToList() : Rules;

        var files = CodeReviewPaths.GetFiles(repositoryRoot, roots);
        // Resolve the entity catalog from the repo's referenced assemblies unless a caller injected one
        // (tests and embedding hosts can supply a catalog directly to stay hermetic / skip reflection).
        // The reflection resolve (~1s on a large repo) is independent of all the source parsing below and
        // only feeds the rule loop at the very end, so kick it off now and let it run concurrently with the
        // parse-heavy phases (warm cache / DEE analysis / call graph); we await it just before the rule loop.
        var resolvedFromRepo = entityCatalog is null;
        var entityCatalogTask = resolvedFromRepo
            ? Task.Run(() => EntityCatalogResolver.Resolve(repositoryRoot))
            : Task.FromResult(entityCatalog!);

        if (files.Count == 0)
        {
            // stderr, not Log.*: Log writes to stdout, and scan's stdout is a machine-readable pure-JSON
            // contract. Loud on purpose -- GetFiles silently skips roots that don't exist on disk, so a
            // repository whose layout can't be classified (see CodeReviewPaths.Discover) would otherwise
            // report zero findings with no indication the scan never looked at any source at all.
            Console.Error.WriteLine(
                $"warning: no .cs files found under the resolved scan roots ({string.Join(", ", roots)}) beneath '{repositoryRoot}'. " +
                "If this repository doesn't follow the Cmf.<Project>.Business / Cmf.<Project>.Data convention, pass --roots explicitly.");
        }

        // Opt-in phase timings on stderr (stdout is the pure-JSON contract): CODEREVIEW_PROFILE=1.
        var profile = Environment.GetEnvironmentVariable("CODEREVIEW_PROFILE") == "1";
        var stopwatch = profile ? System.Diagnostics.Stopwatch.StartNew() : null;
        void Mark(string phase)
        {
            if (stopwatch is not null)
            {
                Console.Error.WriteLine($"profile: {phase}: {stopwatch.ElapsedMilliseconds}ms");
                stopwatch.Restart();
            }
        }

        var fileCache = new CodeReviewFileCache();
        // Warm the cache in parallel across every file the pre-passes and rule loop will touch:
        // parsing dominates scan time and each file's artifacts are independent, so this is where
        // multicore actually pays. The passes below then run over already-parsed trees.
        Parallel.ForEach(CodeReviewPaths.GetFiles(repositoryRoot, CodeReviewPaths.ResolveRoots(repositoryRoot, ReviewScope.Both)).Concat(files).Distinct(StringComparer.OrdinalIgnoreCase), file =>
        {
            var source = fileCache.Get(file);
            _ = source.Root;
            _ = source.LoopState;
            _ = source.MethodRanges;
        });
        Mark("warm cache");

        var serviceEndpointMap = CmServiceEndpointResolver.GetServiceEndpointMap(repositoryRoot);
        Mark("service endpoints");
        var (deeClassInfo, orchestrationCallSitesInDees) = CmDeeAnalyzer.GetDeeClassInfoAndOrchestrationCallSites(repositoryRoot, fileCache);
        var (_, deeInvocationMap) = CmDeeAnalyzer.GetConstantsAndDeeInvocationMap(repositoryRoot, deeClassInfo.Names, fileCache);
        Mark("dee analysis");
        var callGraph = CmCallGraphBuilder.BuildCallGraph(repositoryRoot, fileCache);
        Mark("call graph");

        // Await the reflection resolve started up front. If the parse phases above took longer than the
        // resolve (the common case on a real repo), this wait is ~0 -- the reflection cost is hidden.
        entityCatalog = entityCatalogTask.Result;
        Mark("entity catalog (overlapped wait)");
        if (resolvedFromRepo && entityCatalog.Names.Count == 0)
        {
            Console.Error.WriteLine(
                "warning: no entity types could be resolved by reflection (is the repository restored? " +
                "expected obj/project.assets.json referencing the Cmf platform assemblies). " +
                "Entity-aware rules (DirectEntityInstantiation, ExcessiveLazyLoading, DeleteRemoveInsideLoop, ...) will not fire.");
        }

        var loopExposureCache = new ConcurrentDictionary<string, LoopExposure?>(StringComparer.Ordinal);
        var deeClassNameByFile = deeClassInfo.FileByName.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.OrdinalIgnoreCase);

        HashSet<string>? changedFileSet = changedFiles is null
            ? null
            : new HashSet<string>(changedFiles.Select(NormalizePathForComparison), StringComparer.OrdinalIgnoreCase);

        var filesToReport = changedFileSet is null
            ? files
            : files.Where(f =>
                changedFileSet.Contains(NormalizePathForComparison(CodeReviewPaths.GetRelativePath(repositoryRoot, f))) ||
                changedFileSet.Contains(NormalizePathForComparison(f)))
                .ToList();

        // Per-file work is independent (all shared state -- caches, maps, settings, baseline -- is
        // read-only or thread-safe), so the rule loop fans out across cores. AsOrdered keeps the
        // pre-sort finding order identical to the sequential loop's; combined with the stable
        // OrderBy below, parallel and sequential scans produce byte-identical output.
        var findings = filesToReport
            .AsParallel().AsOrdered()
            .SelectMany(file => FindFileFindings(file, fileCache, callGraph, loopExposureCache, settings, baseline,
                serviceEndpointMap, deeClassNameByFile, deeInvocationMap, orchestrationCallSitesInDees, activeRules, repositoryRoot, entityCatalog))
            .ToList();
        Mark("rule loop");

        return findings
            .OrderBy(f => f.Severity switch { Severity.Critical => 0, Severity.High => 1, _ => 2 })
            .ThenBy(f => f.RelativePath, StringComparer.Ordinal)
            .ThenBy(f => f.LineNumber)
            .ToList();
    }

    private static List<Finding> FindFileFindings(string file, CodeReviewFileCache fileCache, MethodCallGraph callGraph,
        ConcurrentDictionary<string, LoopExposure?> loopExposureCache, CodeReviewSettings? settings, CodeReviewBaseline? baseline,
        Dictionary<string, ServiceEndpointInfo> serviceEndpointMap, Dictionary<string, string> deeClassNameByFile,
        Dictionary<string, List<DeeInvocationRecord>> deeInvocationMap, Dictionary<string, OrchestrationCallSite> orchestrationCallSitesInDees,
        IReadOnlyList<IReviewRule> activeRules, string repositoryRoot, EntityCatalog entityCatalog)
    {
        var findings = new List<Finding>();
        {
            var source = fileCache.Get(file);
            var lines = source.Lines;
            var context = RuleFileContext.Build(source, callGraph, loopExposureCache, entityCatalog);

            CrossReference? deeCrossReference = null;
            if (context.IsDeeFile)
            {
                var deeClassNameForFile = deeClassNameByFile.TryGetValue(file, out var name) ? name : null;

                if (deeClassNameForFile is not null && deeInvocationMap.TryGetValue(deeClassNameForFile, out var callers) && callers.Count > 0)
                {
                    var firstCaller = callers[0];
                    string? callerEndpoint = null;
                    if (firstCaller.CallerMethodName is not null && serviceEndpointMap.TryGetValue(firstCaller.CallerMethodName, out var endpointInfo))
                    {
                        callerEndpoint = endpointInfo.Endpoint;
                    }

                    deeCrossReference = new CrossReference
                    {
                        Kind = CrossReference.CalledByOrchestrationKind,
                        Location = $"{firstCaller.CallerRelativePath}:{firstCaller.CallerLine}",
                        Method = firstCaller.CallerMethodName,
                        Endpoint = callerEndpoint,
                        ExtraCount = Math.Max(0, callers.Count - 1)
                    };
                }
                else
                {
                    deeCrossReference = new CrossReference { Kind = CrossReference.NoCallerFoundKind };
                }
            }

            var relativePath = CodeReviewPaths.GetRelativePath(repositoryRoot, file);

            foreach (var rule in activeRules)
            {
                foreach (var match in rule.FindMatches(context))
                {
                    if (settings?.IsRuleEnabled(match.RuleName) == false)
                    {
                        continue;
                    }

                    if (CodeReviewSuppressions.IsSuppressed(lines, match.Line, match.RuleName))
                    {
                        continue;
                    }

                    var snippet = match.Line >= 1 && match.Line <= lines.Length ? lines[match.Line - 1].Trim() : string.Empty;
                    if (baseline?.Contains(relativePath, match.RuleName, snippet) == true)
                    {
                        continue;
                    }

                    var methodName = context.GetMethodNameForLine(match.Line);

                    string? serviceEndpoint = null;
                    string? serviceEndpointSource = null;
                    if (context.IsOrchestrationFile && methodName is not null && serviceEndpointMap.TryGetValue(methodName, out var svcEndpointInfo))
                    {
                        serviceEndpoint = svcEndpointInfo.Endpoint;
                        serviceEndpointSource = svcEndpointInfo.SourceLocation;
                    }

                    var crossReference = deeCrossReference;
                    if (!context.IsDeeFile && methodName is not null && serviceEndpoint is null && orchestrationCallSitesInDees.TryGetValue(methodName, out var deeCallSite))
                    {
                        crossReference = new CrossReference
                        {
                            Kind = CrossReference.CalledByDeeKind,
                            Location = $"{deeCallSite.CallerRelativePath}:{deeCallSite.CallerLine}"
                        };
                    }

                    var loopExposure = methodName is not null ? context.GetLoopExposure(methodName) : null;

                    findings.Add(new Finding
                    {
                        FilePath = file,
                        RelativePath = relativePath,
                        LineNumber = match.Line,
                        Severity = settings?.GetSeverityOverride(match.RuleName) ?? match.Severity,
                        Rule = match.RuleName,
                        Description = match.Description,
                        Snippet = snippet,
                        SuggestedFix = match.SuggestedFix,
                        MethodName = methodName,
                        ServiceEndpoint = serviceEndpoint,
                        ServiceEndpointSource = serviceEndpointSource,
                        CrossReference = crossReference,
                        LoopExposure = loopExposure,
                        Escalation = match.Escalation
                    });
                }
            }
        }

        return findings;
    }
}
