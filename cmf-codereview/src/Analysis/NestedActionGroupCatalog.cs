using System.Reflection;
using System.Text.Json;
using Cmf.Cli.Plugin.CodeReview.Models;

namespace Cmf.Cli.Plugin.CodeReview;

/// <summary>
/// Resolves which other Action Groups a given Action Group's execution is known to also trigger, via a
/// small curated table of direct edges (an Action Group's candidate method name -&gt; the candidate method
/// names of every Action Group it's confirmed to call into). This exists because <see cref="MethodCallGraph"/>
/// only ever sees the customer's own <c>Cmf.Custom.*</c> source -- a nested Action Group entirely inside
/// the compiled CM Framework platform (e.g. <c>ComplexTrackOutMaterials</c> calling <c>TrackOutMaterials</c>,
/// which wraps <c>MaterialCollection.TrackOut</c>) can never be discovered by walking source, since there's
/// no source to walk. The table is hand-confirmed against the platform's own source once and shipped as an
/// embedded default; a project can correct or extend it via <c>.codereview.json</c>'s <c>nestedActionGroups</c>
/// section (see configuration) -- an entry there fully replaces the
/// shipped entry for the same <see cref="NestedActionGroupEdge.Outer"/> key, and adds a new one for any
/// other key, so a project can both fix a stale/wrong edge and add its own confirmed ones.
/// </summary>
public static class NestedActionGroupCatalog
{
    private const string EmbeddedResourceName = "Cmf.Cli.Plugin.CodeReview.Data.nested-action-groups.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>The shipped default edges, embedded in the assembly so they always travel with a published build.</summary>
    public static List<NestedActionGroupEdge> LoadDefaults()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(EmbeddedResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{EmbeddedResourceName}' not found.");

        return JsonSerializer.Deserialize<List<NestedActionGroupEdge>>(stream, SerializerOptions) ?? new();
    }

    /// <summary>
    /// Merges the shipped defaults with a project's overrides: an override entry replaces the default
    /// entry for the same <see cref="NestedActionGroupEdge.Outer"/> (a correction), or is added as a new
    /// entry otherwise (an extension). Entries with a null/blank <see cref="NestedActionGroupEdge.Outer"/>
    /// are skipped.
    /// </summary>
    public static Dictionary<string, List<string>> BuildEdgeMap(
        IEnumerable<NestedActionGroupEdge> defaults, IEnumerable<NestedActionGroupEdge>? overrides = null)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var edge in defaults)
        {
            if (!string.IsNullOrWhiteSpace(edge.Outer))
            {
                map[edge.Outer] = edge.Nested;
            }
        }

        foreach (var edge in overrides ?? Enumerable.Empty<NestedActionGroupEdge>())
        {
            if (!string.IsNullOrWhiteSpace(edge.Outer))
            {
                map[edge.Outer] = edge.Nested;
            }
        }

        return map;
    }

    /// <summary>
    /// Every Action Group candidate method name transitively reachable from <paramref name="candidateMethodName"/>
    /// by walking <paramref name="edgeMap"/> breadth-first -- e.g. resolving a 2-edge chain
    /// (<c>ComplexTrackOutAndMoveMaterialsToNextStep</c> -&gt; <c>ComplexTrackOutMaterials</c>,
    /// <c>ComplexMoveMaterialsToNextStep</c>, each of which has its own further edge) down to the full,
    /// flattened 4-entry result, without needing that flattening hand-authored into the table. A visited
    /// set guards against a cyclical edge (shouldn't happen in a hand-curated table, but a bad
    /// project-supplied override could introduce one).
    /// </summary>
    public static List<string> ResolveTransitiveTriggered(string candidateMethodName, IReadOnlyDictionary<string, List<string>> edgeMap)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal) { candidateMethodName };
        var result = new List<string>();
        var queue = new Queue<string>();
        queue.Enqueue(candidateMethodName);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!edgeMap.TryGetValue(current, out var nestedNames))
            {
                continue;
            }

            foreach (var nested in nestedNames)
            {
                if (!visited.Add(nested))
                {
                    continue;
                }

                result.Add(nested);
                queue.Enqueue(nested);
            }
        }

        return result;
    }
}
