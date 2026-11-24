namespace Cmf.Cli.Plugin.CodeReview.Models;

/// <summary>A distinct DEE-to-Action-Group wiring and its highest-version MasterData source.</summary>
public class ActionGroupInfo
{
    public required string ActionGroup { get; init; }
    public required string SourceLocation { get; init; }
}
