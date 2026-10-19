using System.Text.Json;
using Cmf.Cli.Plugin.CodeReview.Models;

namespace Cmf.Cli.Plugin.CodeReview;

/// <summary>Shared findings JSON serialization/writing, used by both 'scan' and 'review'.</summary>
public static class FindingsIo
{
    public static string Serialize(List<Finding> findings) =>
        JsonSerializer.Serialize(findings, new JsonSerializerOptions { WriteIndented = true });

    public static void WriteToFile(string path, string json)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, json);
    }
}
