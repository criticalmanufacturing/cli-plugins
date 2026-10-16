using System.Text.Json;
using Cmf.CLI.Utilities;
using Cmf.Cli.Plugin.CodeReview.Models;

namespace Cmf.Cli.Plugin.CodeReview;

/// <summary>One accepted (baselined) finding: matched by file + rule + flagged code, not line number, so unrelated edits that shift lines don't un-baseline it.</summary>
public class BaselineEntry
{
    public required string RelativePath { get; init; }
    public required string Rule { get; init; }
    public required string Snippet { get; init; }
}

/// <summary>
/// Optional accepted-debt baseline, read from <c>.codereview.baseline.json</c> at the scanned
/// repository's root. Findings matching a baseline entry are excluded from scan output, so a team
/// can adopt the tool on an existing codebase and only gate on NEW findings. Regenerate with
/// <c>scan --update-baseline</c>.
/// </summary>
public class CodeReviewBaseline
{
    public const string FileName = ".codereview.baseline.json";

    public List<BaselineEntry> Findings { get; set; } = new();

    private HashSet<(string, string, string)>? _keys;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static CodeReviewBaseline? Load(string repositoryRoot)
    {
        var path = Path.Combine(repositoryRoot, FileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<CodeReviewBaseline>(File.ReadAllText(path), SerializerOptions);
        }
        catch (JsonException e)
        {
            throw new CliException($"Could not parse {path}: {e.Message}");
        }
    }

    public static string Write(string repositoryRoot, IEnumerable<Finding> findings)
    {
        var baseline = new CodeReviewBaseline
        {
            Findings = findings
                .Select(f => new BaselineEntry { RelativePath = NormalizePath(f.RelativePath), Rule = f.Rule, Snippet = f.Snippet.Trim() })
                .DistinctBy(e => (e.RelativePath, e.Rule, e.Snippet))
                .OrderBy(e => e.RelativePath, StringComparer.Ordinal).ThenBy(e => e.Rule, StringComparer.Ordinal)
                .ToList()
        };

        var path = Path.Combine(repositoryRoot, FileName);
        File.WriteAllText(path, JsonSerializer.Serialize(baseline, SerializerOptions));
        return path;
    }

    public bool Contains(string relativePath, string rule, string snippet)
    {
        _keys ??= Findings
            .Select(e => (NormalizePath(e.RelativePath), e.Rule, e.Snippet.Trim()))
            .ToHashSet();
        return _keys.Contains((NormalizePath(relativePath), rule, snippet.Trim()));
    }

    private static string NormalizePath(string path) => path.Trim().Replace('\\', '/');
}
