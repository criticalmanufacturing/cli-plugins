using System.Text.Json;
using Cmf.Cli.Plugin.CodeReview.Models;

namespace Cmf.Cli.Plugin.CodeReview;

/// <summary>Shared findings JSON serialization/writing, used by both 'scan' and 'review'.</summary>
public static class FindingsIo
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>The default remains the legacy findings array for existing pipeline consumers.</summary>
    public static string Serialize(List<Finding> findings) =>
        JsonSerializer.Serialize(findings, Options);

    /// <summary>Explicitly selects the enriched object contract, preserving an empty recorded summary.</summary>
    public static string Serialize(List<Finding> findings, List<ActionGroupSummary> actionGroupSummaries) =>
        JsonSerializer.Serialize(new FindingsFile { Findings = findings, ActionGroupSummaries = actionGroupSummaries }, Options);

    public static FindingsFile Deserialize(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind == JsonValueKind.Array)
        {
            return new FindingsFile { Findings = JsonSerializer.Deserialize<List<Finding>>(json, Options)! };
        }

        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty(nameof(FindingsFile.Findings), out var findings) ||
            findings.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Expected a findings array or an object containing a Findings array.");
        }
        return JsonSerializer.Deserialize<FindingsFile>(json, Options)!;
    }

    public static void WriteToFile(string path, string json)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, json);
    }
}
