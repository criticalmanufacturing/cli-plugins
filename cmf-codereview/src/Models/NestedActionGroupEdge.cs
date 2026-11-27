namespace Cmf.Cli.Plugin.CodeReview.Models;

/// <summary>
/// One curated, hand-confirmed fact: executing the Action Group whose candidate method name is
/// <see cref="Outer"/> also triggers every Action Group candidate method name listed in
/// <see cref="Nested"/> (see <see cref="Cmf.Cli.Plugin.CodeReview.NestedActionGroupCatalog"/>). Direct
/// edges only -- multi-hop chains are resolved by walking these transitively, not by flattening them by
/// hand into one entry.
/// </summary>
public class NestedActionGroupEdge
{
    public string? Outer { get; set; }
    public List<string> Nested { get; set; } = new();
    /// <summary>Free-text provenance note (e.g. which platform source method call was inspected to confirm this edge). Never read programmatically.</summary>
    public string? Comment { get; set; }
}
