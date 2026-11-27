using System.CommandLine;
using System.CommandLine.NamingConventionBinder;
using Cmf.CLI.Core.Attributes;
using Cmf.CLI.Core.Commands;
using Cmf.CLI.Utilities;
using Cmf.Cli.Plugin.CodeReview.Enums;
using Cmf.Cli.Plugin.CodeReview.PullRequests;

namespace Cmf.Cli.Plugin.CodeReview.Commands;

[CmfCommand("pr-review", Description = "Scan changes and publish inline comments for an Azure DevOps pull request.")]
public class PrReviewCommand : BaseCommand
{
    public override void Configure(Command cmd)
    {
        cmd.AddOption(new Option<string>("--repository-root", description: "Root of the PR checkout") { IsRequired = true });
        cmd.AddOption(new Option<string?>("--output", description: "Path to save the complete findings JSON"));
        cmd.AddOption(new Option<string?>("--fail-on", description: "Fail on findings at or above Critical, High, or Medium. Omit or pass an empty value to only comment."));
        cmd.Handler = CommandHandler.Create<string, string?, string?>(ExecuteAsync);
    }

    public async Task<int> ExecuteAsync(string repositoryRoot, string? output, string? failOn)
    {
        Severity? threshold = null;
        if (!string.IsNullOrWhiteSpace(failOn))
        {
            var severityName = failOn.Trim();
            if (!Enum.GetNames<Severity>().Contains(severityName, StringComparer.OrdinalIgnoreCase))
            {
                throw new CliException("--fail-on must be Critical, High, Medium, or empty.");
            }

            threshold = Enum.Parse<Severity>(severityName, ignoreCase: true);
        }

        var changes = await GitChanges.ReadAsync(repositoryRoot);
        var findings = CmFrameworkScanner.GetFindings(repositoryRoot, changedFiles: changes.Files, addedFiles: changes.AddedFiles);
        var json = FindingsIo.Serialize(findings);
        if (!string.IsNullOrWhiteSpace(output))
        {
            FindingsIo.WriteToFile(output, json);
        }

        Console.WriteLine(json);
        if (threshold is { } minimum && CmFrameworkScanner.AnyAtOrAboveSeverity(findings, minimum))
        {
            Console.Error.WriteLine($"Code review failed: findings at or above {minimum} were found in changed files.");
            return 1;
        }

        var selected = findings.Where(finding => changes.ContainsLine(finding.RelativePath, finding.LineNumber)).ToList();
        if (selected.Count == 0)
        {
            Console.WriteLine($"No findings on lines changed by this pull request ({findings.Count} total findings in changed files).");
            return 0;
        }

        using var httpClient = new HttpClient();
        var publisher = AzureDevOpsComments.FromPipeline(httpClient);
        await publisher.PublishAsync(selected, findings.Count);
        return 0;
    }
}
