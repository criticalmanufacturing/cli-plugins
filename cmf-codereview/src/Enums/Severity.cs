using System.Text.Json.Serialization;

namespace Cmf.Cli.Plugin.CodeReview.Enums;

/// <summary>
/// Serialized by NAME in all JSON output ("Medium"/"High"/"Critical") -- the numeric order here is
/// an internal comparison detail (Medium &lt; High &lt; Critical) and reads backwards to anyone
/// assuming the P0-style "lower is worse" convention, so it must never leak into the JSON contract.
/// The converter still accepts numbers on read, so scan files written before this change load fine.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum Severity
{
    Medium,
    High,
    Critical
}
