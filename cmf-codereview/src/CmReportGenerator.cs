using System.Text;
using Cmf.Cli.Plugin.CodeReview.Models;
using Cmf.Cli.Plugin.CodeReview.Enums;

namespace Cmf.Cli.Plugin.CodeReview;

/// <summary>Renders a list of findings into either a severity-grouped ("Standard") or rule-grouped ("Detailed") Markdown report.</summary>
public static class CmReportGenerator
{
    internal static readonly Severity[] SeverityOrder = { Severity.Critical, Severity.High, Severity.Medium };

    internal static readonly Dictionary<Severity, string> SeverityIcons = new()
    {
        [Severity.Critical] = "\U0001F534",
        [Severity.High] = "\U0001F7E0",
        [Severity.Medium] = "\U0001F7E1"
    };

    private static string FormatSnippet(string snippet) => snippet.Replace("|", "\\|").Trim();

    internal static string FormatPath(string path) => path.Replace('\\', '/');

    internal static string FormatContext(string? methodName, string? serviceEndpoint)
    {
        var method = string.IsNullOrWhiteSpace(methodName) ? "n/a" : methodName;
        var endpoint = string.IsNullOrWhiteSpace(serviceEndpoint) ? "n/a" : serviceEndpoint;
        return $"Method: {method}; Endpoint: {endpoint}";
    }

    internal static string FormatEndpointSource(string? endpointSource) =>
        string.IsNullOrWhiteSpace(endpointSource) ? "n/a" : FormatPath(endpointSource);

    /// <summary>
    /// The parenthetical clause appended to a rule's suggested-direction text explaining why a group
    /// of findings sits at Critical (escalated) or High (baseline) severity. Empty for rules that
    /// don't escalate. Shared by the Markdown and HTML report generators so the wording stays in sync.
    /// </summary>
    internal static string BuildEscalationReasonClause(IReadOnlyCollection<Finding> ruleGroup, Severity severity, string rule)
    {
        if (!CmFrameworkScanner.GetEscalatableRules().Contains(rule))
        {
            return string.Empty;
        }

        if (severity != Severity.Critical)
        {
            return " (baseline: single loop, not nested, and the enclosing method is not itself called from another loop)";
        }

        var nestedCount = ruleGroup.Count(f => f.Description.Contains("nested loop"));
        var compoundCount = ruleGroup.Count(f => f.Description.Contains("compounding the cost further"));
        var reasonParts = new List<string>();
        if (nestedCount > 0) reasonParts.Add($"{nestedCount} inside a nested loop");
        if (compoundCount > 0) reasonParts.Add($"{compoundCount} whose method is also called from another loop");
        return " (escalated from High: " + string.Join("; ", reasonParts) + " — see per-finding descriptions below for which)";
    }

    internal static string FormatCrossReference(CrossReference? crossReference)
    {
        if (crossReference is null || string.IsNullOrWhiteSpace(crossReference.Kind))
        {
            return "n/a";
        }

        switch (crossReference.Kind)
        {
            case CrossReference.CalledByOrchestrationKind:
            {
                var location = FormatPath(crossReference.Location ?? string.Empty);
                var method = string.IsNullOrWhiteSpace(crossReference.Method) ? "n/a" : crossReference.Method;
                var suffix = crossReference.ExtraCount > 0 ? $" (+{crossReference.ExtraCount} more)" : string.Empty;

                return string.IsNullOrWhiteSpace(crossReference.Endpoint)
                    ? $"Invoked by {location} (method: {method}); no mapped service endpoint{suffix}"
                    : $"Invoked by {location} (method: {method}) -> Endpoint: {crossReference.Endpoint}{suffix}";
            }
            case CrossReference.CalledByDeeKind:
                return $"Called by DEE: {FormatPath(crossReference.Location ?? string.Empty)}";
            case CrossReference.NoCallerFoundKind:
                return "No direct code caller found (may be triggered only via Action Group/Event Rule configuration)";
            default:
                return "n/a";
        }
    }

    /// <summary>Renders findings grouped by severity (Critical → High → Medium), with a code snippet and suggested fix per finding.</summary>
    public static string BuildStandardReport(IReadOnlyList<Finding> findings, string title, string summary)
    {
        var headers = new List<string>
        {
            $"## {title}", "", "### Summary", summary, "", "### Findings", ""
        };

        var body = new List<string>();
        foreach (var severity in SeverityOrder)
        {
            var sectionTitle = severity switch
            {
                Severity.Critical => "#### \U0001F534 CRITICAL",
                Severity.High => "#### \U0001F7E0 HIGH",
                _ => "#### \U0001F7E1 MEDIUM"
            };

            body.Add(sectionTitle);
            body.Add("");
            body.Add("| File name and line numbers | Method / Endpoint | Endpoint Source | Cross-Reference | Description | Code snippet |");
            body.Add("|---|---|---|---|---|---|");

            var group = findings.Where(f => f.Severity == severity).ToList();
            if (group.Count == 0)
            {
                body.Add("| None | None | None | None | None | None |");
                body.Add("");
                continue;
            }

            foreach (var finding in group)
            {
                var location = $"{FormatPath(finding.RelativePath)} (line {finding.LineNumber})";
                var context = FormatContext(finding.MethodName, finding.ServiceEndpoint);
                var endpointSource = FormatEndpointSource(finding.ServiceEndpointSource);
                var crossReference = FormatCrossReference(finding.CrossReference);
                var description = $"[{finding.Rule}] {finding.Description} Fix: {finding.SuggestedFix}";
                var snippet = FormatSnippet(finding.Snippet);
                body.Add($"| {location} | {context} | {endpointSource} | {crossReference} | {description} | {snippet} |");
            }

            body.Add("");
        }

        var all = new List<string>();
        all.AddRange(headers);
        all.AddRange(body);
        all.Add("---");
        all.Add("");
        return string.Join(Environment.NewLine, all);
    }

    /// <summary>Renders findings grouped by rule, with a rule summary table and full cross-reference/loop-exposure detail per finding.</summary>
    public static string BuildDetailedReport(IReadOnlyList<Finding> findings, string title, string summary)
    {
        var lines = new List<string>
        {
            $"# Detailed Remediation Backlog — {title}", "", summary, "", "## Rule Summary", "",
            "| Rule | Severity | Count | Suggested direction |",
            "|---|---|---:|---|"
        };

        var ruleSeverityGroups = findings
            .GroupBy(f => (f.Rule, f.Severity))
            .OrderBy(g => g.Key.Rule, StringComparer.Ordinal)
            .ThenBy(g => Array.IndexOf(SeverityOrder, g.Key.Severity))
            .ToList();

        if (ruleSeverityGroups.Count == 0)
        {
            lines.Add("| None | None | 0 | None |");
        }
        else
        {
            foreach (var ruleGroup in ruleSeverityGroups)
            {
                var sample = ruleGroup.First();
                var reasonClause = BuildEscalationReasonClause(ruleGroup.ToList(), sample.Severity, sample.Rule);

                lines.Add($"| {sample.Rule} | {SeverityIcons[sample.Severity]} {sample.Severity} | {ruleGroup.Count()} | {sample.SuggestedFix}{reasonClause} |");
            }
        }

        foreach (var severity in SeverityOrder)
        {
            var severityFindings = findings.Where(f => f.Severity == severity).ToList();
            lines.Add("");
            lines.Add($"## {SeverityIcons[severity]} {severity.ToString().ToUpperInvariant()}");
            lines.Add("");

            if (severityFindings.Count == 0)
            {
                lines.Add("No findings.");
                continue;
            }

            foreach (var ruleGroup in severityFindings.GroupBy(f => f.Rule).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var sample = ruleGroup.First();
                lines.Add($"### {ruleGroup.Key} ({ruleGroup.Count()})");
                lines.Add("");
                lines.Add(sample.SuggestedFix);
                lines.Add("");
                lines.Add("| File name and line numbers | Method / Endpoint | Endpoint Source | Cross-Reference | Description | Code snippet |");
                lines.Add("|---|---|---|---|---|---|");

                foreach (var finding in ruleGroup.OrderBy(f => f.RelativePath, StringComparer.Ordinal).ThenBy(f => f.LineNumber))
                {
                    var location = $"{FormatPath(finding.RelativePath)} (line {finding.LineNumber})";
                    var context = FormatContext(finding.MethodName, finding.ServiceEndpoint);
                    var endpointSource = FormatEndpointSource(finding.ServiceEndpointSource);
                    var crossReference = FormatCrossReference(finding.CrossReference);
                    var description = finding.Description;
                    var snippet = FormatSnippet(finding.Snippet);
                    lines.Add($"| {location} | {context} | {endpointSource} | {crossReference} | {description} | {snippet} |");
                }

                lines.Add("");
            }
        }

        lines.Add("---");
        lines.Add("");
        return string.Join(Environment.NewLine, lines);
    }
}
