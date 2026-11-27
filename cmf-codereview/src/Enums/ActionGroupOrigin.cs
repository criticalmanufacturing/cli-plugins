using System.Text.Json.Serialization;

namespace Cmf.Cli.Plugin.CodeReview.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ActionGroupOrigin
{
    Custom,
    Oob,
    Unknown
}
