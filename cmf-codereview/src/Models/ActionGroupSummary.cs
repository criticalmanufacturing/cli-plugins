using Cmf.Cli.Plugin.CodeReview.Enums;

namespace Cmf.Cli.Plugin.CodeReview.Models;

/// <summary>MasterData wiring counts, including DEEs that have no findings.</summary>
public class ActionGroupSummary
{
    public required string ActionGroup { get; init; }
    public required int DeeCount { get; init; }
    public required ActionGroupRiskLevel RiskLevel { get; init; }
    public required IReadOnlyList<string> Dees { get; init; }

    /// <summary>Possible callers in the static, name-based graph; empty if no graph was supplied.</summary>
    public IReadOnlyList<string> PossibleServiceEndpoints { get; init; } = new List<string>();

    /// <summary>Reserved for origin classification; this foundation leaves origin unknown.</summary>
    public ActionGroupOrigin Origin { get; init; } = ActionGroupOrigin.Unknown;
}
