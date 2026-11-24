using Cmf.CLI.Core;
using Cmf.CLI.Core.Attributes;
using Cmf.CLI.Core.Commands;
using System.CommandLine;
using System.CommandLine.NamingConventionBinder;
using Cmf.Cli.Plugin.CodeReview.Enums;

namespace Cmf.Cli.Plugin.CodeReview.Commands;

/// <summary>cmf-codereview review — one-shot: scan the code and write findings JSON and a Markdown, HTML, or SARIF report.</summary>
[CmfCommand("review", Description = "One-shot: scan the code and write findings JSON and a Markdown, HTML, or SARIF report.")]
public class ReviewCommand : BaseCommand
{
    public override void Configure(Command cmd)
    {
        var repositoryRootOption = new Option<string>("--repository-root", "Root of the repository to scan") { IsRequired = true };
        var scopeOption = new Option<ReviewScope>("--scope", () => ReviewScope.Both, "Which areas to scan: DEEs, Orchestration, or Both");
        var rootsOption = new Option<string[]>("--roots", "Explicit relative roots to scan instead of --scope") { AllowMultipleArgumentsPerToken = true };
        var findingsPathOption = new Option<string?>("--findings-path", "Where to write the intermediate findings JSON");
        var reportPathOption = new Option<string?>("--report-path", "Output path for the report");
        var reportModeOption = new Option<ReportMode>("--report-mode", () => ReportMode.Standard, "Standard or Detailed report shape");
        var reportFormatOption = new Option<ReportFormat>("--report-format", () => ReportFormat.Markdown, "Markdown (portable, for PR comments), Html (self-contained browser report), or Sarif (SARIF 2.1.0 log)");
        var titleOption = new Option<string>("--title", () => ReportCommand.DefaultTitle, "Report title");
        var summaryOption = new Option<string>("--summary", () => ReportCommand.DefaultSummary, "Report summary line");
        var includeActionGroupSummaryOption = new Option<bool>("--include-action-group-summary",
            "Write findings as an enriched JSON envelope. The report always includes the scan's Action Group summary.");

        cmd.AddOption(repositoryRootOption);
        cmd.AddOption(scopeOption);
        cmd.AddOption(rootsOption);
        cmd.AddOption(findingsPathOption);
        cmd.AddOption(reportPathOption);
        cmd.AddOption(reportModeOption);
        cmd.AddOption(reportFormatOption);
        cmd.AddOption(titleOption);
        cmd.AddOption(summaryOption);
        cmd.AddOption(includeActionGroupSummaryOption);

        cmd.Handler = CommandHandler.Create((Action<string, ReviewScope, string[], string?, string?, ReportMode, ReportFormat, string, string, bool>)Execute);
    }

    public void Execute(string repositoryRoot, ReviewScope scope, string[] roots, string? findingsPath, string? reportPath, ReportMode reportMode, ReportFormat reportFormat, string title, string summary, bool includeActionGroupSummary = false)
    {
        var effectiveRoots = (roots is { Length: > 0 } ? roots : CodeReviewPaths.ResolveRoots(repositoryRoot, scope)).ToList();

        if (string.IsNullOrWhiteSpace(findingsPath))
        {
            var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            findingsPath = Path.Combine(CodeReviewPaths.GetDefaultReportsDirectory(repositoryRoot), $"cm-framework-scan-{timestamp}.json");
        }

        var findings = CmFrameworkScanner.GetFindings(repositoryRoot, effectiveRoots, null, null, true, null, out var actionGroupSummaries);
        FindingsIo.WriteToFile(findingsPath, includeActionGroupSummary
            ? FindingsIo.Serialize(findings, actionGroupSummaries) : FindingsIo.Serialize(findings));

        var resolvedReportPath = ReportCommand.Build(findingsPath, repositoryRoot, reportPath, reportMode, reportFormat, title, summary, actionGroupSummaries);

        var scopeLabel = CodeReviewPaths.GetScopeLabel(repositoryRoot, effectiveRoots);
        Log.Information($"Scope: {scopeLabel}");
        Log.Information($"ReportMode: {reportMode}");
        Log.Information($"ReportFormat: {reportFormat}");
        Log.Information($"FindingsPath: {findingsPath}");
        Log.Information($"ReportPath: {resolvedReportPath}");
    }
}
