using System.Text.Json;
using Cmf.Cli.Plugin.CodeReview.Enums;
using Cmf.Cli.Plugin.CodeReview.Models;

namespace Cmf.Cli.Plugin.CodeReview;

/// <summary>Resolves all distinct (Action, ActionGroup) pairs from versioned MasterData JSON.</summary>
public static class MasterDataActionGroupResolver
{
    /// <summary>
    /// Accumulates every wiring. Only an exact repeated pair is replaced: the highest numeric version
    /// wins globally across roots, then the ordinally greatest repository-relative path for a tie.
    /// Optional roots allow callers to supply an explicit MasterData root set in any order.
    /// Process Rule removal analysis is a separate migration step; only JSON additions are modeled here.
    /// </summary>
    public static Dictionary<string, List<ActionGroupInfo>> GetActionGroupMap(
        string repositoryRoot, IEnumerable<string>? masterDataRoots = null)
    {
        var map = new Dictionary<string, Dictionary<string, ActionGroupInfo>>(StringComparer.Ordinal);
        var files = (masterDataRoots ?? CodeReviewPaths.DiscoverMasterDataRoots(repositoryRoot))
            .Select(root => Path.GetFullPath(Path.Combine(repositoryRoot, root)))
            .Distinct(StringComparer.Ordinal)
            .Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateDirectories(root)
                .Select(dir => (Directory: dir, Version: ParseVersion(Path.GetFileName(dir))))
                .Where(entry => entry.Version is not null)
                .SelectMany(entry => Directory.EnumerateFiles(entry.Directory, "*.json")
                    .Select(file => (File: file, entry.Version, RelativePath: CodeReviewPaths.GetRelativePath(repositoryRoot, file).Replace('\\', '/')))))
            .OrderBy(entry => entry.Version)
            .ThenBy(entry => entry.RelativePath, StringComparer.Ordinal);

        foreach (var entry in files)
        {
            foreach (var (action, group) in ReadEntries(entry.File))
            {
                if (!map.TryGetValue(action, out var groups))
                {
                    groups = new Dictionary<string, ActionGroupInfo>(StringComparer.Ordinal);
                    map[action] = groups;
                }

                groups[group] = new ActionGroupInfo { ActionGroup = group, SourceLocation = entry.RelativePath };
            }
        }

        return map.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Values.OrderBy(info => info.ActionGroup, StringComparer.Ordinal).ToList(),
            StringComparer.Ordinal);
    }

    // Normalize omitted components so 2.10 and 2.10.0 are equal numeric versions.
    private static Version? ParseVersion(string name) => Version.TryParse(name, out var version)
        ? new Version(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision))
        : null;

    private static List<(string Action, string Group)> ReadEntries(string file)
    {
        var entries = new List<(string, string)>();
        try
        {
            using var stream = File.OpenRead(file);
            using var doc = JsonDocument.Parse(stream);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("DEEActionOrder", out var orders) || orders.ValueKind != JsonValueKind.Object)
            {
                return entries;
            }

            foreach (var entry in orders.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object) continue;
                var action = GetString(entry.Value, "Action");
                var group = GetString(entry.Value, "ActionGroup");
                if (!string.IsNullOrWhiteSpace(action) && !string.IsNullOrWhiteSpace(group)) entries.Add((action, group));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Stdout belongs to the machine-readable scan result.
            Console.Error.WriteLine($"warning: skipping unreadable/malformed MasterData file: {file} ({ex.Message})");
        }
        return entries;
    }

    private static string? GetString(JsonElement entry, string property) =>
        entry.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>Counts each DEE under every group it is wired to, regardless of findings or scan filters.</summary>
    public static List<ActionGroupSummary> Summarize(
        IReadOnlyDictionary<string, List<ActionGroupInfo>> actionGroupMap,
        MethodCallGraph? callGraph = null,
        IReadOnlyDictionary<string, ServiceEndpointInfo>? serviceEndpointMap = null) =>
        actionGroupMap.SelectMany(pair => pair.Value.Select(info => (Dee: pair.Key, Info: info)))
            .GroupBy(pair => pair.Info.ActionGroup, StringComparer.Ordinal)
            .Select(group =>
            {
                var dees = group.Select(pair => pair.Dee).Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal).ToList();
                return new ActionGroupSummary
                {
                    ActionGroup = group.Key,
                    DeeCount = dees.Count,
                    Dees = dees,
                    RiskLevel = dees.Count switch
                    {
                        <= 1 => ActionGroupRiskLevel.Low,
                        2 => ActionGroupRiskLevel.Medium,
                        3 => ActionGroupRiskLevel.High,
                        _ => ActionGroupRiskLevel.Critical
                    },
                    PossibleServiceEndpoints = ResolveEndpoints(group.Key, callGraph, serviceEndpointMap)
                };
            })
            .OrderByDescending(summary => summary.DeeCount)
            .ThenBy(summary => summary.ActionGroup, StringComparer.Ordinal).ToList();

    private static List<string> ResolveEndpoints(string group, MethodCallGraph? graph,
        IReadOnlyDictionary<string, ServiceEndpointInfo>? endpoints)
    {
        if (graph is null || endpoints is null) return new List<string>();
        var segments = group.Split('.');
        if (segments.Length < 2) return new List<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { segments[^2] };
        var found = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        queue.Enqueue(segments[^2]);
        while (queue.TryDequeue(out var method))
        {
            if (endpoints.TryGetValue(method, out var endpoint)) found.Add(endpoint.Endpoint);
            if (!graph.CallersByMethod.TryGetValue(method, out var callers)) continue;
            foreach (var caller in callers)
            {
                if (visited.Add(caller.CallerMethodName)) queue.Enqueue(caller.CallerMethodName);
            }
        }
        return found.OrderBy(endpoint => endpoint, StringComparer.Ordinal).ToList();
    }
}
