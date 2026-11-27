using System.Text.Json.Serialization;

namespace Cmf.Cli.Plugin.CodeReview.Enums;

/// <summary>Coupling risk from distinct DEEs sharing an Action Group, independent of finding severity.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ActionGroupRiskLevel
{
    Low,
    Medium,
    High,
    Critical
}
