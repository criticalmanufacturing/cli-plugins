using Cmf.Cli.Plugin.CodeReview.Enums;

namespace Cmf.Cli.Plugin.CodeReview.Models;

/// <summary>
/// Machine-readable record of WHY a finding's severity was raised above its rule's base severity
/// (nested loops multiply the cost; a caller-side loop compounds it). The same reasons also appear
/// as prose in the finding's description for human readers -- this field exists so dashboards and
/// CI gates never have to parse that prose. Null on findings that fired at base severity.
/// </summary>
public class Escalation
{
    public const string NestedLoopReason = "NestedLoop";
    public const string CalledFromLoopReason = "CalledFromLoopElsewhere";

    /// <summary>The severity this finding would have had without the compounding factors.</summary>
    public Severity BaseSeverity { get; init; }

    /// <summary>One or more of the reason constants on this class.</summary>
    public required List<string> Reasons { get; init; }

    /// <summary>Loop nesting depth at the finding's line; set only when <see cref="NestedLoopReason"/> applies.</summary>
    public int? NestedLoopDepth { get; init; }
}
