namespace Cmf.Cli.Plugin.CodeReview.Models;

public class LoopState
{
    public HashSet<int> Lines { get; } = new();
    public Dictionary<int, int> DepthByLine { get; } = new();

    /// <summary>
    /// Lines at loop depth 1 where control-flow analysis proves the loop body always exits
    /// (via return/throw/break, never falls through or continues) once it reaches that line --
    /// so a call there runs at most once despite textually sitting inside a loop.
    /// </summary>
    public HashSet<int> SingleExecutionLines { get; } = new();
}
