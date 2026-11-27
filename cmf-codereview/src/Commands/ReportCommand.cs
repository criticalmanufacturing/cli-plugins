using Cmf.CLI.Core;
using Cmf.CLI.Core.Attributes;
using Cmf.CLI.Core.Commands;
using Cmf.CLI.Utilities;
using System.CommandLine;
using System.CommandLine.NamingConventionBinder;
using Cmf.Cli.Plugin.CodeReview.Models;
using Cmf.Cli.Plugin.CodeReview.Enums;

namespace Cmf.Cli.Plugin.CodeReview.Commands;

/// <summary>cmf-codereview report — renders a Markdown, HTML, or SARIF report from findings JSON.</summary>
[CmfCommand("report", Description = "Render a Markdown, HTML, or SARIF report from a findings JSON file.")]
public class ReportCommand : BaseCommand
{
    public const string DefaultTitle = "C# Code Review for CM Framework Scan";
    public const string DefaultSummary = "Automated CM framework scan for looped loads, smart table resolves, config access, direct entity construction, and related review heuristics.";

    public override void Configure(Command cmd)
    {
        var findingsPathOption = new Option<string>("--findings-path", "Path to a findings JSON file produced by 'scan'") { IsRequired = true };
        var repositoryRootOption = new Option<string?>("--repository-root", "Repository root for the default report path and MasterData summary fallback for legacy findings");
        var reportPathOption = new Option<string?>("--report-path", "Output path for the report; defaults to Tools/CodeReview/Reports/cm-framework-scan-<mode>-<timestamp>.md under --repository-root");
        var reportModeOption = new Option<ReportMode>("--report-mode", () => ReportMode.Standard, "Standard (severity-grouped) or Detailed (rule backlog) report shape");
        var reportFormatOption = new Option<ReportFormat>("--report-format", () => ReportFormat.Markdown, "Markdown (portable, for PR comments), Html (self-contained browser report), or Sarif (SARIF 2.1.0 log)");
        var titleOption = new Option<string>("--title", () => DefaultTitle, "Report title");
        var summaryOption = new Option<string>("--summary", () => DefaultSummary, "Report summary line");
        var stdoutOption = new Option<bool>("--stdout", "Print the report to stdout instead of writing a file (e.g. for piping into a PR comment). No file is created when this is set.");

        cmd.AddOption(findingsPathOption);
        cmd.AddOption(repositoryRootOption);
        cmd.AddOption(reportPathOption);
        cmd.AddOption(reportModeOption);
        cmd.AddOption(reportFormatOption);
        cmd.AddOption(titleOption);
        cmd.AddOption(summaryOption);
        cmd.AddOption(stdoutOption);

        cmd.Handler = CommandHandler.Create<string, string?, string?, ReportMode, ReportFormat, string, string, bool>(Execute);
    }

    public void Execute(string findingsPath, string? repositoryRoot, string? reportPath, ReportMode reportMode, ReportFormat reportFormat, string title, string summary, bool stdout)
    {
        if (stdout)
        {
            Console.WriteLine(BuildContent(findingsPath, reportMode, reportFormat, title, summary, repositoryRoot));
        }
        else
        {
            var resolvedReportPath = Build(findingsPath, repositoryRoot, reportPath, reportMode, reportFormat, title, summary);
            Console.WriteLine(resolvedReportPath);
        }
    }

    /// <summary>Reads a findings JSON file and renders it to the requested format, without resolving or writing to any file path.</summary>
    public static string BuildContent(string findingsPath, ReportMode reportMode, ReportFormat reportFormat, string title, string summary,
        string? repositoryRoot = null, IReadOnlyList<ActionGroupSummary>? actionGroupSummaries = null)
    {
        if (!File.Exists(findingsPath))
        {
            throw new CliException($"Findings file not found: {findingsPath}");
        }

        var json = File.ReadAllText(findingsPath);
        var findingsFile = FindingsIo.Deserialize(json);
        var findings = findingsFile.Findings;
        actionGroupSummaries ??= findingsFile.ActionGroupSummaries;
        string? contextNote = null;
        if (actionGroupSummaries is null)
        {
            contextNote = "Action Group scan context was not recorded in this findings file. Origin remains Unknown because declaration indexes are unavailable. Possible Service Endpoints are unavailable; empty endpoint lists do not mean that no endpoint can reach a group. " +
                "Use scan --include-action-group-summary or review to retain scan context. Supplying --repository-root reconstructs wiring counts from current MasterData only.";
            if (!string.IsNullOrWhiteSpace(repositoryRoot))
            {
                actionGroupSummaries = MasterDataActionGroupResolver.Summarize(MasterDataActionGroupResolver.GetActionGroupMap(repositoryRoot));
            }
        }

        return (reportMode, reportFormat) switch
        {
            (ReportMode.Detailed, ReportFormat.Html) => CmHtmlReportGenerator.BuildDetailedReport(findings, title, summary, actionGroupSummaries, contextNote),
            (ReportMode.Detailed, ReportFormat.Sarif) => CmSarifReportGenerator.BuildDetailedReport(findings, title, summary, actionGroupSummaries, contextNote),
            (ReportMode.Detailed, _) => CmReportGenerator.BuildDetailedReport(findings, title, summary, actionGroupSummaries, contextNote),
            (_, ReportFormat.Html) => CmHtmlReportGenerator.BuildStandardReport(findings, title, summary, actionGroupSummaries, contextNote),
            (_, ReportFormat.Sarif) => CmSarifReportGenerator.BuildStandardReport(findings, title, summary, actionGroupSummaries, contextNote),
            _ => CmReportGenerator.BuildStandardReport(findings, title, summary, actionGroupSummaries, contextNote)
        };
    }

    /// <summary>Shared by the 'report' command and the 'review' one-shot command.</summary>
    public static string Build(string findingsPath, string? repositoryRoot, string? reportPath, ReportMode reportMode, ReportFormat reportFormat, string title, string summary,
        IReadOnlyList<ActionGroupSummary>? actionGroupSummaries = null)
    {
        var content = BuildContent(findingsPath, reportMode, reportFormat, title, summary, repositoryRoot, actionGroupSummaries);

        if (string.IsNullOrWhiteSpace(reportPath))
        {
            var root = repositoryRoot ?? Directory.GetCurrentDirectory();
            var reportDirectory = CodeReviewPaths.GetDefaultReportsDirectory(root);
            Directory.CreateDirectory(reportDirectory);
            var suffix = reportMode == ReportMode.Detailed ? "detailed" : "standard";
            var extension = reportFormat switch
            {
                ReportFormat.Html => "html",
                ReportFormat.Sarif => "sarif",
                _ => "md"
            };
            var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            reportPath = Path.Combine(reportDirectory, $"cm-framework-scan-{suffix}-{timestamp}.{extension}");
        }

        var directory = Path.GetDirectoryName(reportPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(reportPath, content);
        return reportPath;
    }
}
