using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cmf.CLI.Utilities;
using Cmf.Cli.Plugin.CodeReview.Models;
using Cmf.Cli.Plugin.CodeReview.Enums;

namespace Cmf.Cli.Plugin.CodeReview;

internal record DataPackageContent(string Directory, string ContentType, string Source, int Order);

/// <summary>Resolves default folder roots and scope labels, and enumerates .cs files to scan under the repository.</summary>
public static class CodeReviewPaths
{
    // Folder names that are never worth descending into while searching for cmfpackage.json manifests --
    // build output, VCS metadata, and restored packages can be enormous and are never where a manifest lives.
    private static readonly string[] ManifestSearchNoiseDirectoryNames =
        { ".git", ".hg", ".svn", "bin", "obj", "node_modules", "packages", ".vs", ".vscode", "TestResults", "Debug", "Release" };

    // A manifest never lives more than a few folders deep from the repository root in any real CM
    // Framework project (business/data package -> content-type subfolder is the deepest observed
    // shape); bounding the walk keeps discovery fast even on a repository with large, deep,
    // manifest-free trees (NuGet package caches, generated output, etc.) elsewhere.
    private const int ManifestSearchMaxDepth = 6;

    private record ResolvedRoots(string[] OrchestrationRoots, string[] DeeRoots);

    private record PackageManifest(string Directory, string? PackageType, List<(string ContentType, string Source)> ContentToPack);

    // Discovery walks the repository tree and parses every cmfpackage.json found -- not free, but
    // ResolveRoots is called many times per scan (cache warming, DEE analysis, call graph, the scan
    // itself), so memoize per repository root for the lifetime of the process.
    private static readonly ConcurrentDictionary<string, ResolvedRoots> RootsCache = new(StringComparer.OrdinalIgnoreCase);

    // File-classification regexes are structural (any "Cmf.<Project>.Business/Cmf.<Project>.Orchestration/"
    // shaped segment matches), not tied to the literal "Custom" project name -- so a customization
    // project using any other name (e.g. "Cmf.Semi.*") is classified correctly. Both the nested
    // Micron-style DEEs path (Cmf.<X>.Data/Cmf.<X>.Data.Packages/DEEs) and the flatter convention
    // (Cmf.<X>.Data/DEEs) are matched. Kept independent of manifest-based root discovery -- these
    // classify individual files by shape, not by resolving package metadata.
    private static readonly Regex OrchestrationSegmentRegex =
        new(@"Cmf\.[^\\/]+\.Business[\\/]Cmf\.[^\\/]+\.Orchestration[\\/]", RegexOptions.Compiled);

    private static readonly Regex DeeSegmentRegex =
        new(@"Cmf\.[^\\/]+\.Data[\\/](?:Cmf\.[^\\/]+\.Data\.Packages[\\/])?DEEs[\\/]", RegexOptions.Compiled);

    private static readonly Regex ProcessRulesSegmentRegex =
        new(@"Cmf\.[^\\/]+\.Data[\\/](?:Cmf\.[^\\/]+\.Data\.Packages[\\/])?DEEs[\\/]ProcessRules[\\/]", RegexOptions.Compiled);

    /// <summary>Returns the default relative folder roots for the given scope (DEEs, Orchestration, or Both), for this repository.</summary>
    public static string[] ResolveRoots(string repositoryRoot, ReviewScope scope)
    {
        var resolved = Discover(repositoryRoot);

        return scope switch
        {
            ReviewScope.DEEs => resolved.DeeRoots,
            ReviewScope.Orchestration => resolved.OrchestrationRoots,
            _ => resolved.OrchestrationRoots.Concat(resolved.DeeRoots).ToArray()
        };
    }

    internal static IEnumerable<DataPackageContent> DiscoverDataPackageContent(string repositoryRoot) =>
        FindAndParseManifests(repositoryRoot)
            .Where(m => string.Equals(m.PackageType, "Data", StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => m.Directory, StringComparer.Ordinal)
            .SelectMany(m => m.ContentToPack.Select((c, order) => new DataPackageContent(m.Directory, c.ContentType, c.Source, order)));

    internal static bool IsProcessRuleContent(string contentType) =>
        contentType.Equals("EntityTypes", StringComparison.OrdinalIgnoreCase) ||
        contentType.Equals("ProcessRulesPre", StringComparison.OrdinalIgnoreCase) ||
        contentType.Equals("ProcessRulesPost", StringComparison.OrdinalIgnoreCase);

    private static ResolvedRoots Discover(string repositoryRoot)
    {
        var fullRoot = Path.GetFullPath(repositoryRoot);
        return RootsCache.GetOrAdd(fullRoot, DiscoverUncached);
    }

    /// <summary>
    /// Discovers scan roots from the repository's own <c>cmfpackage.json</c> package manifests --
    /// the authoritative source, not a guess from folder-name conventions:
    /// <list type="bullet">
    /// <item>Orchestration/Common/Services roots come from every manifest whose <c>packageType</c> is
    /// <c>"Business"</c> -- those three folders sit as siblings inside that manifest's own directory.</item>
    /// <item>DEE roots come from every manifest whose <c>packageType</c> is <c>"Data"</c>: each
    /// <c>contentToPack</c> entry with <c>contentType: "DEE"</c> gives a <c>source</c> glob (e.g.
    /// <c>"DEEs/*"</c>) whose literal (non-wildcard) directory prefix, resolved against the manifest's
    /// own directory, is the DEE root.</item>
    /// </list>
    /// This is project-name-agnostic (works for "Custom", "Semi", or anything else) and shape-agnostic
    /// (nested <c>*.Data.Packages</c> or flat <c>Data/DEEs</c> -- whatever the manifest's own source
    /// path says). <c>cmfpackage.json</c> is the only supported project marker -- a repository with none
    /// resolving to a Business or Data package is not a CM Framework customization project this tool
    /// can scan, so discovery fails loudly (<see cref="CliException"/>) instead of guessing.
    /// </summary>
    private static ResolvedRoots DiscoverUncached(string fullRepositoryRoot)
    {
        if (!Directory.Exists(fullRepositoryRoot))
        {
            throw new CliException($"Repository root not found: {fullRepositoryRoot}");
        }

        var manifests = FindAndParseManifests(fullRepositoryRoot);

        var orchestrationRoots = manifests
            .Where(m => string.Equals(m.PackageType, "Business", StringComparison.OrdinalIgnoreCase))
            .SelectMany(m => FindOrchestrationSiblingFolders(m.Directory))
            .Select(dir => GetRelativePath(fullRepositoryRoot, dir))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var deeRoots = manifests
            .Where(m => string.Equals(m.PackageType, "Data", StringComparison.OrdinalIgnoreCase))
            .SelectMany(m => m.ContentToPack
                .Where(c => string.Equals(c.ContentType, "DEE", StringComparison.OrdinalIgnoreCase) || IsProcessRuleContent(c.ContentType))
                .Select(c => Path.Combine(m.Directory, ExtractLiteralDirectory(c.Source)))
                .Where(Directory.Exists))
            .Select(dir => GetRelativePath(fullRepositoryRoot, dir))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // A DEE root often already contains its Process Rule roots. Do not scan those files twice.
        deeRoots = deeRoots.Where(root => !deeRoots.Any(other =>
            !other.Equals(root, StringComparison.OrdinalIgnoreCase) &&
            root.StartsWith(other + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))).ToArray();

        if (orchestrationRoots.Length == 0 && deeRoots.Length == 0)
        {
            throw new CliException(
                $"No cmfpackage.json under '{fullRepositoryRoot}' resolves to a Business or Data package " +
                "(packageType \"Business\" or \"Data\" with a DEE contentToPack entry). " +
                "cmf-codereview only supports CM Framework customization repositories that use cmfpackage.json manifests -- " +
                "pass --roots explicitly to scan specific folders regardless.");
        }

        return new ResolvedRoots(orchestrationRoots, deeRoots);
    }

    private static IEnumerable<string> FindOrchestrationSiblingFolders(string businessDir)
    {
        if (!Directory.Exists(businessDir))
        {
            yield break;
        }

        var subNames = SafeEnumerateDirectories(businessDir).Select(Path.GetFileName).ToList();

        foreach (var suffix in new[] { ".Orchestration", ".Common", ".Services" })
        {
            var match = subNames.FirstOrDefault(n => n is not null && n.EndsWith(suffix, StringComparison.Ordinal));
            if (match is not null)
            {
                yield return Path.Combine(businessDir, match);
            }
        }
    }

    /// <summary>The literal (non-wildcard) leading directory portion of a contentToPack source glob, e.g. "DEEs/*" -&gt; "DEEs".</summary>
    private static string ExtractLiteralDirectory(string source)
    {
        var normalized = source.Replace('\\', '/');
        var literalSegments = new List<string>();

        foreach (var segment in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.Contains('*') || segment.Contains('?') || segment.Contains('$'))
            {
                break;
            }

            literalSegments.Add(segment);
        }

        return literalSegments.Count > 0 ? Path.Combine(literalSegments.ToArray()) : string.Empty;
    }

    /// <summary>
    /// Finds every cmfpackage.json under the repository (bounded depth, skipping build/VCS/package-cache
    /// noise folders) and parses each in parallel -- there are only ever a handful of these per
    /// repository, but the search fans out across top-level folders concurrently to stay fast on a
    /// large tree, per-branch traversal being the only serial part of the walk.
    /// </summary>
    private static List<PackageManifest> FindAndParseManifests(string repositoryRoot)
    {
        var manifestFiles = new ConcurrentBag<string>();

        var rootManifest = Path.Combine(repositoryRoot, "cmfpackage.json");
        if (File.Exists(rootManifest))
        {
            manifestFiles.Add(rootManifest);
        }

        Parallel.ForEach(SafeEnumerateDirectories(repositoryRoot), dir => WalkForManifests(dir, 1, manifestFiles));

        return manifestFiles
            .AsParallel()
            .Select(ParseManifest)
            .Where(m => m is not null)
            .Select(m => m!)
            .ToList();
    }

    private static void WalkForManifests(string dir, int depth, ConcurrentBag<string> results)
    {
        var name = Path.GetFileName(dir);
        if (name is not null && ManifestSearchNoiseDirectoryNames.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        var manifestPath = Path.Combine(dir, "cmfpackage.json");
        if (File.Exists(manifestPath))
        {
            results.Add(manifestPath);
        }

        if (depth >= ManifestSearchMaxDepth)
        {
            return;
        }

        foreach (var sub in SafeEnumerateDirectories(dir))
        {
            WalkForManifests(sub, depth + 1, results);
        }
    }

    private static PackageManifest? ParseManifest(string manifestPath)
    {
        try
        {
            using var stream = File.OpenRead(manifestPath);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;

            var packageType = root.TryGetProperty("packageType", out var packageTypeProp) && packageTypeProp.ValueKind == JsonValueKind.String
                ? packageTypeProp.GetString()
                : null;

            var contentToPack = new List<(string ContentType, string Source)>();
            if (root.TryGetProperty("contentToPack", out var contentToPackProp) && contentToPackProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in contentToPackProp.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var contentType = entry.TryGetProperty("contentType", out var ct) && ct.ValueKind == JsonValueKind.String ? ct.GetString() : null;
                    var source = entry.TryGetProperty("source", out var src) && src.ValueKind == JsonValueKind.String ? src.GetString() : null;

                    if (contentType is not null && source is not null)
                    {
                        contentToPack.Add((contentType, source));
                    }
                }
            }

            return new PackageManifest(Path.GetDirectoryName(manifestPath)!, packageType, contentToPack);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string dir)
    {
        try
        {
            return Directory.EnumerateDirectories(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Enumerable.Empty<string>();
        }
    }

    /// <summary>Returns <paramref name="path"/> expressed relative to <paramref name="basePath"/>.</summary>
    public static string GetRelativePath(string basePath, string path)
    {
        var baseFullPath = Path.GetFullPath(basePath);
        var pathFullPath = Path.GetFullPath(path);
        return Path.GetRelativePath(baseFullPath, pathFullPath);
    }

    /// <summary>Recursively enumerates every .cs file under each of the given roots (or the "Both" default roots), silently skipping roots that don't exist on disk.</summary>
    public static List<string> GetFiles(string repositoryRoot, IEnumerable<string>? roots = null)
    {
        roots ??= ResolveRoots(repositoryRoot, ReviewScope.Both);
        var files = new List<string>();

        foreach (var root in roots)
        {
            var fullRoot = Path.Combine(repositoryRoot, root);
            if (!Directory.Exists(fullRoot))
            {
                continue;
            }

            files.AddRange(Directory.EnumerateFiles(fullRoot, "*.cs", SearchOption.AllDirectories));
        }

        return files.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f, StringComparer.Ordinal).ToList();
    }

    /// <summary>True if the file sits under a Business/&lt;Project&gt;.Orchestration folder, for any Cmf.&lt;Project&gt; naming.</summary>
    public static bool IsOrchestrationFile(string path) => OrchestrationSegmentRegex.IsMatch(path);

    /// <summary>True if the file sits under a Data/DEEs folder (nested under a Data.Packages folder or directly), for any Cmf.&lt;Project&gt; naming.</summary>
    public static bool IsDeeFile(string path) => DeeSegmentRegex.IsMatch(path);

    /// <summary>True if the file sits under the DEEs/ProcessRules root -- one-time migration code, exempt from a couple of otherwise-real-time-only rules.</summary>
    public static bool IsOneTimeMigrationFile(string path) => ProcessRulesSegmentRegex.IsMatch(path);

    /// <summary>True if the file is an extension-method file by folder or filename convention.</summary>
    public static bool IsExtensionFile(string path) =>
        path.Contains($"Extensions{Path.DirectorySeparatorChar}") || path.Contains("Extensions.");

    /// <summary>The default output folder for scan findings/reports under a repository, used by 'scan'/'report'/'review' when no explicit output path is given.</summary>
    public static string GetDefaultReportsDirectory(string repositoryRoot) =>
        Path.GetFullPath(Path.Combine(repositoryRoot, "Tools", "CodeReview", "Reports"));

    /// <summary>Classifies a root set back into DEEs/Orchestration/Both/Custom, for this repository.</summary>
    public static string GetScopeLabel(string repositoryRoot, IReadOnlyCollection<string> roots)
    {
        var normalized = roots.OrderBy(r => r, StringComparer.Ordinal).ToList();

        bool SameAs(ReviewScope scope)
        {
            var candidate = ResolveRoots(repositoryRoot, scope).OrderBy(r => r, StringComparer.Ordinal).ToList();
            return normalized.SequenceEqual(candidate, StringComparer.Ordinal);
        }

        if (SameAs(ReviewScope.DEEs)) return "DEEs";
        if (SameAs(ReviewScope.Orchestration)) return "Orchestration";
        if (SameAs(ReviewScope.Both)) return "Both";
        return "Custom";
    }
}
