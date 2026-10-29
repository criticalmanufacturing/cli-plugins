namespace Cmf.Cli.Plugin.CodeReview.Enums;

/// <summary>
/// Output rendering for a report. Markdown is the portable default (PR comments, plain viewers);
/// Html produces a self-contained page for triaging findings; Sarif produces a SARIF 2.1.0 log.
/// </summary>
public enum ReportFormat
{
    Markdown,
    Html,
    Sarif
}
