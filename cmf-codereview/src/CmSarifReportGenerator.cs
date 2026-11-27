using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cmf.Cli.Plugin.CodeReview.Models;
using Cmf.Cli.Plugin.CodeReview.Enums;

namespace Cmf.Cli.Plugin.CodeReview;

/// <summary>Renders findings as a SARIF 2.1.0 log for compatible static analysis viewers.</summary>
public static class CmSarifReportGenerator
{
    private const string ToolName = "cmf-codereview";
    private const string InformationUri = "https://github.com/criticalmanufacturing/cli-plugins/tree/main/cmf-codereview";

    private static string SarifLevel(Severity severity) => severity switch
    {
        Severity.Critical => "error",
        Severity.High => "error",
        Severity.Medium => "warning",
        _ => "note"
    };

    private static string FormatSarifUri(string path) =>
        string.Join("/", path.Replace('\\', '/').Split('/').Select(Uri.EscapeDataString));

    /// <summary>Same signature as CmReportGenerator/CmHtmlReportGenerator so it slots into ReportCommand's switch. `title`/`summary` are accepted for signature parity but unused — SARIF has no concept of either.</summary>
    public static string BuildStandardReport(IReadOnlyList<Finding> findings, string title, string summary,
        IReadOnlyList<ActionGroupSummary>? actionGroupSummaries = null, string? actionGroupContextNote = null) => Build(findings, actionGroupSummaries, actionGroupContextNote);

    /// <summary>SARIF has no rule-grouped/severity-grouped distinction like Markdown/Html do — Detailed renders identically to Standard.</summary>
    public static string BuildDetailedReport(IReadOnlyList<Finding> findings, string title, string summary,
        IReadOnlyList<ActionGroupSummary>? actionGroupSummaries = null, string? actionGroupContextNote = null) => Build(findings, actionGroupSummaries, actionGroupContextNote);

    private static string Build(IReadOnlyList<Finding> findings, IReadOnlyList<ActionGroupSummary>? actionGroupSummaries, string? contextNote)
    {
        var toolVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

        var rules = findings
            .Select(f => f.Rule)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(r => r, StringComparer.Ordinal)
            .Select(rule => new JsonObject
            {
                ["id"] = rule,
                ["shortDescription"] = new JsonObject { ["text"] = rule }
            })
            .ToArray();

        var results = findings.Select(f =>
        {
            var physicalLocation = new JsonObject
            {
                ["artifactLocation"] = new JsonObject
                {
                    ["uri"] = FormatSarifUri(f.RelativePath)
                }
            };

            if (f.LineNumber > 0)
            {
                physicalLocation["region"] = new JsonObject
                {
                    ["startLine"] = f.LineNumber
                };
            }

            return new JsonObject
            {
                ["ruleId"] = f.Rule,
                ["level"] = SarifLevel(f.Severity),
                ["message"] = new JsonObject { ["text"] = $"{f.Description} Fix: {f.SuggestedFix}" },
                ["properties"] = new JsonObject
                {
                    ["actionGroups"] = JsonSerializer.SerializeToNode(f.ActionGroups),
                    ["actionGroupSources"] = JsonSerializer.SerializeToNode(f.ActionGroupSources)
                },
                ["locations"] = new JsonArray(new JsonObject { ["physicalLocation"] = physicalLocation })
            };
        }).ToArray();

        var sarif = new JsonObject
        {
            ["$schema"] = "https://docs.oasis-open.org/sarif/sarif/v2.1.0/errata01/os/schemas/sarif-schema-2.1.0.json",
            ["version"] = "2.1.0",
            ["runs"] = new JsonArray(
                new JsonObject
                {
                    ["tool"] = new JsonObject
                    {
                        ["driver"] = new JsonObject
                        {
                            ["name"] = ToolName,
                            ["version"] = toolVersion,
                            ["informationUri"] = InformationUri,
                            ["rules"] = new JsonArray(rules.Cast<JsonNode?>().ToArray())
                        }
                    },
                    ["results"] = new JsonArray(results.Cast<JsonNode?>().ToArray())
                })
        };

        if (actionGroupSummaries is not null || contextNote is not null)
        {
            // Wiring risk is context, not an extra diagnostic or a finding severity.
            var properties = new JsonObject();
            if (actionGroupSummaries is not null) properties["actionGroupSummaries"] = JsonSerializer.SerializeToNode(actionGroupSummaries);
            if (contextNote is not null) properties["actionGroupSummaryNote"] = contextNote;
            sarif["runs"]![0]!["properties"] = properties;
        }
        return sarif.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
