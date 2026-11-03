using Cmf.CLI.Core.Attributes;
using Cmf.CLI.Core.Commands;
using System.CommandLine;
using System.CommandLine.NamingConventionBinder;
using Cmf.Cli.Plugin.CodeReview.Enums;

namespace Cmf.Cli.Plugin.CodeReview.Commands;

/// <summary>cmf-codereview scan — runs the review engine and prints/saves findings as JSON or SARIF.</summary>
[CmfCommand("scan", Description = "Scan code and print (and optionally save) findings as JSON or SARIF.")]
public class ScanCommand : BaseCommand
{
    public override void Configure(Command cmd)
    {
        var repositoryRootOption = new Option<string>("--repository-root", description: "Root of the repository to scan")
        {
            IsRequired = true
        };
        var scopeOption = new Option<ReviewScope>("--scope", () => ReviewScope.Both, "Which areas to scan: DEEs, Orchestration, or Both");
        var rootsOption = new Option<string[]>("--roots", description: "Explicit relative roots to scan instead of --scope")
        {
            AllowMultipleArgumentsPerToken = true
        };
        var outputOption = new Option<string?>("--output", description: "Path to write findings in the selected format");
        var changedFilesListOption = new Option<string?>("--changed-files-list", description:
            "Path to a text file listing changed files (one relative or absolute path per line, e.g. from 'git diff --name-only'). " +
            "When given, only findings on those files are reported, though the whole repository is still analyzed for cross-reference/loop-exposure context.");
        var failOnOption = new Option<Severity?>("--fail-on", description:
            "If given, exit with a non-zero code when any reported finding is at or above this severity. If omitted, the command always exits 0 regardless of findings.");
        var updateBaselineOption = new Option<bool>("--update-baseline", description:
            "Write all current findings (ignoring any existing baseline) to .codereview.baseline.json at the repository root, accepting them as known debt — " +
            "subsequent scans only report findings not in the baseline. Always exits 0.");

        var reportFormatOption = new Option<string>("--report-format", () => "Json", "Findings output format: Json (default) or Sarif");
        reportFormatOption.AddValidator(result =>
        {
            var value = result.GetValueForOption(reportFormatOption);
            if (value is not null &&
                !value.Equals("json", StringComparison.OrdinalIgnoreCase) &&
                !value.Equals("sarif", StringComparison.OrdinalIgnoreCase))
            {
                result.ErrorMessage = $"--report-format must be Json or Sarif. Value received: '{value}'.";
            }
        });

        cmd.AddOption(repositoryRootOption);
        cmd.AddOption(scopeOption);
        cmd.AddOption(rootsOption);
        cmd.AddOption(outputOption);
        cmd.AddOption(changedFilesListOption);
        cmd.AddOption(failOnOption);
        cmd.AddOption(updateBaselineOption);
        cmd.AddOption(reportFormatOption);

        cmd.Handler = CommandHandler.Create<string, ReviewScope, string[], string?, string?, Severity?, bool, string?>(Execute);
    }

    public int Execute(string repositoryRoot, ReviewScope scope, string[] roots, string? output, string? changedFilesList, Severity? failOn, bool updateBaseline, string? reportFormat = null)
    {
        var effectiveRoots = roots is { Length: > 0 } ? roots : CodeReviewPaths.ResolveRoots(repositoryRoot, scope);
        var changedFiles = string.IsNullOrWhiteSpace(changedFilesList)
            ? null
            : File.ReadAllLines(changedFilesList).Select(line => line.Trim()).Where(line => line.Length > 0);

        var findings = CmFrameworkScanner.GetFindings(repositoryRoot, effectiveRoots, changedFiles, applyBaseline: !updateBaseline);

        if (updateBaseline)
        {
            var baselinePath = CodeReviewBaseline.Write(repositoryRoot, findings);
            // stderr, not Log.*: Log writes to stdout, and scan's stdout is a machine-readable
            // contract (pure findings JSON) that pipelines parse directly.
            Console.Error.WriteLine($"Baseline written: {baselinePath} ({findings.Count} findings accepted)");
        }

        var json = reportFormat?.Equals("sarif", StringComparison.OrdinalIgnoreCase) == true
            ? CmSarifReportGenerator.BuildStandardReport(findings, ReportCommand.DefaultTitle, ReportCommand.DefaultSummary)
            : FindingsIo.Serialize(findings);

        if (!string.IsNullOrWhiteSpace(output))
        {
            FindingsIo.WriteToFile(output, json);
        }

        Console.WriteLine(json);

        return !updateBaseline && failOn.HasValue && CmFrameworkScanner.AnyAtOrAboveSeverity(findings, failOn.Value) ? 1 : 0;
    }
}
