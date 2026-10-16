namespace Cmf.Cli.Plugin.CodeReview.Models;

public class MethodRange
{
    public required string MethodName { get; init; }
    public int StartLine { get; init; }
    public int StartDepth { get; init; }
    public int EndLine { get; set; }
}
