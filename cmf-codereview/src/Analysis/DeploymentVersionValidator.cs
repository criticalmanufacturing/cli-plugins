using System.Text.Json;
using System.Text.RegularExpressions;
using Cmf.Cli.Plugin.CodeReview.Enums;
using Cmf.Cli.Plugin.CodeReview.Models;

namespace Cmf.Cli.Plugin.CodeReview;

/// <summary>Release-folder checks apply only to explicitly added/renamed files, never historical directories.</summary>
public static class DeploymentVersionValidator
{
    public static IEnumerable<Finding> GetFindings(string repositoryRoot, IEnumerable<string> addedFiles,
        IEnumerable<string>? changedFiles = null, CodeReviewSettings? settings = null, CodeReviewBaseline? baseline = null)
    {
        var root = Path.GetFullPath(repositoryRoot);
        var changed = changedFiles?.Select(p => FullPath(root, p)).Where(p => p is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in addedFiles.Select(p => FullPath(root, p)).Where(p => p is not null).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.Ordinal))
        {
            if (!File.Exists(path) || changed is not null && !changed.Contains(path)) continue;
            var manifest = FindManifest(root, path!);
            if (manifest is null) continue;
            var relative = Path.GetRelativePath(Path.GetDirectoryName(manifest)!, path!).Replace('\\', '/');
            // A malformed owning manifest is actionable only for deployment-content candidates.
            var candidate = relative.StartsWith("MasterData/", StringComparison.OrdinalIgnoreCase) ||
                relative.StartsWith("DEEs/ProcessRules/", StringComparison.OrdinalIgnoreCase);
            string? problem = null, fix = null;
            var rule = "DeploymentVersionUnresolved";
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(manifest), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                var json = document.RootElement;
                if (!json.TryGetProperty("packageType", out var type) || !string.Equals(type.GetString(), "Data", StringComparison.OrdinalIgnoreCase)) continue;
                if (!json.TryGetProperty("contentToPack", out var entries) || entries.ValueKind != JsonValueKind.Array) continue;
                foreach (var entry in entries.EnumerateArray())
                {
                    if (!entry.TryGetProperty("contentType", out var contentType) || contentType.GetString() is not { } content ||
                        !new[] { "MasterData", "EntityTypes", "ProcessRulesPre", "ProcessRulesPost" }.Contains(content, StringComparer.OrdinalIgnoreCase) ||
                        !entry.TryGetProperty("source", out var sourceNode) || sourceNode.GetString() is not { } source) continue;
                    var normalized = source.Replace('\\', '/');
                    if (normalized.StartsWith("./", StringComparison.Ordinal)) normalized = normalized[2..];
                    // Match the version segment independently of its expected value, otherwise a wrong
                    // directory would disappear from the very contentToPack glob we are validating.
                    var segments = normalized.Split('/');
                    var versionIndex = Array.FindIndex(segments, s => s == "$(version)" || Regex.IsMatch(s, @"^\d+\.\d+\.\d+(?:\.\d+)?(?:[-+][^/]+)?$"));
                    if (versionIndex < 0) continue; // Recurring Services and other unversioned content.
                    var pattern = "";
                    for (var i = 0; i < segments.Length; i++)
                    {
                        if (segments[i] == "**" && i < segments.Length - 1) { pattern += "(?:[^/]+/)*"; continue; }
                        pattern += i == versionIndex ? "(?<version>[^/]+)" :
                            Regex.Escape(segments[i]).Replace(@"\*", ".*").Replace(@"\?", ".");
                        if (i < segments.Length - 1) pattern += "/";
                    }
                    var match = Regex.Match(relative, "^" + pattern + "$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
                    if (!match.Success) continue;
                    if (!json.TryGetProperty("version", out var versionNode) || versionNode.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(versionNode.GetString()))
                    {
                        problem = $"Cannot validate this new deployment file: owning manifest '{Path.GetRelativePath(root, manifest)}' has no usable package version.";
                        fix = "Set the Data package version in cmfpackage.json and rerun the review.";
                    }
                    else
                    {
                        var expected = versionNode.GetString()!;
                        var actual = match.Groups["version"];
                        if (string.Equals(actual.Value, expected, StringComparison.Ordinal)) break;
                        var destination = relative[..actual.Index] + expected + relative[(actual.Index + actual.Length)..];
                        rule = "DeploymentVersionMismatch";
                        problem = $"New {content} file uses folder version '{actual.Value}', but owning manifest '{Path.GetRelativePath(root, manifest)}' declares '{expected}'.";
                        fix = $"Move this file to '{Path.GetRelativePath(root, Path.Combine(Path.GetDirectoryName(manifest)!, destination.Replace('/', Path.DirectorySeparatorChar)))}', or correct the package version if the release target is wrong.";
                    }
                    break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            {
                if (candidate)
                {
                    problem = $"Cannot read owning package manifest '{Path.GetRelativePath(root, manifest)}' to validate the new deployment folder version: {ex.Message}";
                    fix = "Correct/restore the package manifest and rerun the review.";
                }
            }
            if (problem is null || settings?.IsRuleEnabled(rule) == false) continue;
            var lines = File.ReadAllLines(path!);
            var snippet = lines.FirstOrDefault()?.Trim() ?? "";
            var relativePath = Path.GetRelativePath(root, path!);
            if (CodeReviewSuppressions.IsSuppressed(lines, 1, rule) || baseline?.Contains(relativePath, rule, snippet) == true) continue;
            yield return new Finding
            {
                FilePath = path!, RelativePath = relativePath, LineNumber = 1,
                Rule = rule, Severity = settings?.GetSeverityOverride(rule) ?? (rule == "DeploymentVersionMismatch" ? Severity.High : Severity.Medium),
                Description = problem, SuggestedFix = fix!, Snippet = snippet
            };
        }
    }

    private static string? FullPath(string root, string path)
    {
        var full = Path.GetFullPath(Path.Combine(root, path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)));
        var relative = Path.GetRelativePath(root, full);
        return relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative) ? null : full;
    }
    private static string? FindManifest(string root, string file)
    {
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(file)!); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "cmfpackage.json");
            if (File.Exists(candidate)) return candidate;
            if (directory.FullName.Equals(root, StringComparison.OrdinalIgnoreCase)) break;
        }
        return null;
    }
}