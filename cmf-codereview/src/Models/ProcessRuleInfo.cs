namespace Cmf.Cli.Plugin.CodeReview.Models;

/// <summary>Deployment classification; Services rules run on every installation.</summary>
public record ProcessRuleInfo(string? Version, string Phase, string? PackageDirectory = null,
    string? ContentType = null, int ContentOrder = -1, string? PackagingProblem = null);
