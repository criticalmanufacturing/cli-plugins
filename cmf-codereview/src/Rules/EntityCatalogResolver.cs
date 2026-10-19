using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Cmf.Cli.Plugin.CodeReview.Rules;

/// <summary>
/// Builds an <see cref="EntityCatalog"/> for a repository by reflecting over the platform (and any
/// other) assemblies it references, instead of a hand-maintained list. An entity's bulk category is
/// read from its base-class chain: EntityInstance/EntityVersion -&gt; Standard, EntityRelation -&gt;
/// Relation, ChildEntityInstance/StateModelFlowInstance -&gt; ChildScoped, CoreBase -&gt; NoBulk.
///
/// Assemblies are located from every <c>obj/project.assets.json</c> under the repository (the same
/// resolved dependency closure the C# compiler uses), so this needs the repo to have been restored.
/// They're loaded with <see cref="MetadataLoadContext"/> -- metadata only, nothing executes -- so a
/// large closure is cheap and safe to inspect. Only <c>Cmf.*</c> assemblies are enumerated for types
/// (entities can only live there); the rest of the closure is loaded purely so base types resolve.
///
/// Anything reflection can't see from metadata stays hand-curated on <see cref="EntityCatalog"/>: the
/// throw-override exception sets and the rule-idiom sets.
/// </summary>
public static class EntityCatalogResolver
{
    private static readonly ConcurrentDictionary<string, EntityCatalog> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Resolves (and memoizes) the entity catalog for a repository. Returns <see cref="EntityCatalog.Empty"/> if no assemblies could be resolved or loading failed -- the caller warns.</summary>
    public static EntityCatalog Resolve(string repositoryRoot) =>
        Cache.GetOrAdd(Path.GetFullPath(repositoryRoot), BuildFromAssemblies);

    private static EntityCatalog BuildFromAssemblies(string fullRepositoryRoot)
    {
        try
        {
            var assemblyPaths = CollectReferencedAssemblyPaths(fullRepositoryRoot);
            if (assemblyPaths.Count == 0)
            {
                return EntityCatalog.Empty;
            }

            // Resolver spans the whole closure plus the running runtime (for System.Private.CoreLib and
            // the BCL), so base-type chains resolve without FileNotFoundException.
            var resolverPaths = new List<string>(assemblyPaths);
            resolverPaths.AddRange(Directory.GetFiles(RuntimeEnvironment.GetRuntimeDirectory(), "*.dll"));
            var resolver = new PathAssemblyResolver(resolverPaths.Distinct(StringComparer.OrdinalIgnoreCase));

            using var mlc = new MetadataLoadContext(resolver, coreAssemblyName: "System.Private.CoreLib");

            // Category by capability rank; on a simple-name collision across namespaces the most-capable
            // wins (matches the historical hand-catalog decision, e.g. the EntityInstance "Service"
            // outranking a CoreBase one, and the common developer intent for the mainstream entity).
            var categories = new Dictionary<string, EntityBulkCategory>(StringComparer.Ordinal);

            foreach (var path in assemblyPaths)
            {
                if (!Path.GetFileName(path).StartsWith("Cmf.", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Type[] types;
                try
                {
                    types = mlc.LoadFromAssemblyPath(path).GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types.Where(t => t is not null).Cast<Type>().ToArray();
                }
                catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or FileNotFoundException)
                {
                    continue;
                }

                foreach (var type in types)
                {
                    if (type is null || !type.IsPublic || string.IsNullOrEmpty(type.Name) ||
                        type.Name.EndsWith("Collection", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (ClassifyByBaseChain(type) is not { } category)
                    {
                        continue;
                    }

                    if (!categories.TryGetValue(type.Name, out var existing) || Rank(category) > Rank(existing))
                    {
                        categories[type.Name] = category;
                    }
                }
            }

            return categories.Count > 0 ? new EntityCatalog(categories) : EntityCatalog.Empty;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"warning: entity catalog reflection failed ({ex.GetType().Name}: {ex.Message}); entity-aware rules will not fire and entity-specific fix text is unavailable.");
            return EntityCatalog.Empty;
        }
    }

    private static int Rank(EntityBulkCategory c) => c switch
    {
        EntityBulkCategory.Standard => 3,
        EntityBulkCategory.ChildScoped => 2,
        EntityBulkCategory.Relation => 1,
        _ => 0
    };

    private static EntityBulkCategory? ClassifyByBaseChain(Type type)
    {
        for (var current = type; current is not null; current = SafeBaseType(current))
        {
            switch (current.Name)
            {
                case "EntityInstance":
                case "EntityVersion":
                    return EntityBulkCategory.Standard;
                case "EntityRelation":
                    return EntityBulkCategory.Relation;
                case "ChildEntityInstance":
                case "StateModelFlowInstance":
                    return EntityBulkCategory.ChildScoped;
                case "CoreBase":
                    return EntityBulkCategory.NoBulk;
            }
        }

        return null;
    }

    private static Type? SafeBaseType(Type type)
    {
        try { return type.BaseType; }
        catch { return null; }
    }

    /// <summary>Every distinct package-assembly path referenced across the repository's project.assets.json files (its restored dependency closure).</summary>
    private static List<string> CollectReferencedAssemblyPaths(string fullRepositoryRoot)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var assetsFile in EnumerateAssetsFiles(fullRepositoryRoot))
        {
            try
            {
                using var stream = File.OpenRead(assetsFile);
                using var doc = JsonDocument.Parse(stream);
                var root = doc.RootElement;

                var packageFolders = root.TryGetProperty("packageFolders", out var pf) && pf.ValueKind == JsonValueKind.Object
                    ? pf.EnumerateObject().Select(p => p.Name).ToList()
                    : new List<string>();
                if (packageFolders.Count == 0)
                {
                    continue;
                }

                var libraryPaths = root.TryGetProperty("libraries", out var libs) && libs.ValueKind == JsonValueKind.Object
                    ? libs.EnumerateObject()
                        .Where(l => l.Value.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String)
                        .ToDictionary(l => l.Name, l => l.Value.GetProperty("path").GetString()!, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                if (!root.TryGetProperty("targets", out var targets) || targets.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var target in targets.EnumerateObject())
                {
                    foreach (var library in target.Value.EnumerateObject())
                    {
                        if (!libraryPaths.TryGetValue(library.Name, out var libraryPath))
                        {
                            continue;
                        }

                        // Prefer the runtime assemblies; fall back to compile-time reference assemblies.
                        var group = library.Value.TryGetProperty("runtime", out var rt) && rt.ValueKind == JsonValueKind.Object ? rt
                            : library.Value.TryGetProperty("compile", out var ct) && ct.ValueKind == JsonValueKind.Object ? ct
                            : default;
                        if (group.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        foreach (var asset in group.EnumerateObject())
                        {
                            var relative = asset.Name.Replace('/', Path.DirectorySeparatorChar);
                            if (!relative.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            foreach (var folder in packageFolders)
                            {
                                var candidate = Path.Combine(folder, libraryPath.Replace('/', Path.DirectorySeparatorChar), relative);
                                if (File.Exists(candidate))
                                {
                                    paths.Add(candidate);
                                    break;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // Skip an unreadable/partial assets file; others may still resolve.
            }
        }

        return paths.ToList();
    }

    private static IEnumerable<string> EnumerateAssetsFiles(string fullRepositoryRoot)
    {
        var results = new List<string>();
        WalkForAssets(fullRepositoryRoot, 0, results);
        return results;
    }

    // project.assets.json always lives at <project>/obj/project.assets.json; bound the walk and skip
    // noise so a large repo doesn't pay for a full-tree scan.
    private const int AssetsSearchMaxDepth = 8;
    private static readonly string[] NoiseDirectoryNames =
        { ".git", ".hg", ".svn", "bin", "node_modules", "packages", ".vs", ".vscode", "TestResults" };

    private static void WalkForAssets(string dir, int depth, List<string> results)
    {
        var name = Path.GetFileName(dir);
        if (!string.IsNullOrEmpty(name) && NoiseDirectoryNames.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        if (string.Equals(name, "obj", StringComparison.OrdinalIgnoreCase))
        {
            var assets = Path.Combine(dir, "project.assets.json");
            if (File.Exists(assets))
            {
                results.Add(assets);
            }
            return; // no project nests under an obj folder
        }

        if (depth >= AssetsSearchMaxDepth)
        {
            return;
        }

        IEnumerable<string> subdirs;
        try { subdirs = Directory.EnumerateDirectories(dir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }

        foreach (var sub in subdirs)
        {
            WalkForAssets(sub, depth + 1, results);
        }
    }
}
