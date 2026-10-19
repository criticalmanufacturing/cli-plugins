namespace Cmf.Cli.Plugin.CodeReview.Enums;

/// <summary>
/// Output rendering for a report. Markdown is the portable default (PR comments, plain viewers);
/// Html produces a single self-contained, filterable, theme-aware page for triaging findings in a browser.
/// </summary>
public enum ReportFormat
{
    Markdown,
    Html
}
