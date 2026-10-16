namespace Cmf.Cli.Plugin.CodeReview.Models;

public class DeeClassInfo
{
    public HashSet<string> Names { get; } = new();
    public Dictionary<string, string> FileByName { get; } = new();
}
