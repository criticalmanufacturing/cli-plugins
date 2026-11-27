using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Cmf.CLI.Utilities;

namespace Cmf.Cli.Plugin.CodeReview.PullRequests;

internal sealed class GitChanges(string[] files, string[] addedFiles, Dictionary<string, List<(int Start, int End)>> lines)
{
    private static readonly Regex HunkHeader = new(@"^@@ -\d+(?:,\d+)? \+(\d+)(?:,(\d+))? @@");
    private static readonly Regex PathEscape = new("""\\([abfnrtv"\\]|[0-7]{3})""");

    public IReadOnlyList<string> Files => files;
    public IReadOnlyList<string> AddedFiles => addedFiles;

    public bool ContainsLine(string path, int line) =>
        lines.TryGetValue(path.Replace(Path.DirectorySeparatorChar, '/').TrimStart('/'), out var ranges) &&
        ranges.Any(range => line >= range.Start && line <= range.End);

    public static async Task<GitChanges> ReadAsync(string repositoryRoot)
    {
        var targetBranch = Environment.GetEnvironmentVariable("SYSTEM_PULLREQUEST_TARGETBRANCH");
        if (string.IsNullOrWhiteSpace(targetBranch))
        {
            var targetName = Environment.GetEnvironmentVariable("SYSTEM_PULLREQUEST_TARGETBRANCHNAME");
            targetBranch = string.IsNullOrWhiteSpace(targetName) ? null : $"refs/heads/{targetName}";
        }

        if (targetBranch is null || !targetBranch.StartsWith("refs/heads/", StringComparison.Ordinal))
        {
            throw new CliException("SYSTEM_PULLREQUEST_TARGETBRANCH must identify the target branch (refs/heads/...).");
        }

        await RunGitAsync(repositoryRoot, ["fetch", "--no-tags", "--", "origin", targetBranch], reader => reader.ReadToEndAsync());
        var targetCommit = (await RunGitAsync(repositoryRoot, ["rev-parse", "--verify", "FETCH_HEAD"], reader => reader.ReadToEndAsync())).Trim();
        var range = $"{targetCommit}...HEAD";
        var names = await RunGitAsync(repositoryRoot, ["diff", "--name-only", "-z", "--find-renames", range, "--"], reader => reader.ReadToEndAsync());
        var addedNames = await RunGitAsync(repositoryRoot, ["diff", "--name-only", "-z", "--find-renames",
            "--diff-filter=AR", range, "--"], reader => reader.ReadToEndAsync());
        var ranges = await RunGitAsync(repositoryRoot, ["-c", "core.quotePath=false", "diff", "--patch", "--unified=0",
            "--find-renames", "--no-color", "--no-ext-diff", "--no-textconv", "--src-prefix=a/", "--dst-prefix=b/", range, "--"], ReadLineRangesAsync);
        return new GitChanges(names.Split('\0', StringSplitOptions.RemoveEmptyEntries),
            addedNames.Split('\0', StringSplitOptions.RemoveEmptyEntries), ranges);
    }

    private static async Task<Dictionary<string, List<(int Start, int End)>>> ReadLineRangesAsync(StreamReader reader)
    {
        var lines = new Dictionary<string, List<(int Start, int End)>>(StringComparer.Ordinal);
        List<(int Start, int End)>? ranges = null;
        var inHeader = false;

        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                ranges = null;
                inHeader = true;
            }
            else if (inHeader && line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                inHeader = false;
                // Git terminates an unquoted path containing spaces with a tab.
                var path = line[4..].TrimEnd('\t');
                if (path.StartsWith('"') && path.EndsWith('"'))
                {
                    path = PathEscape.Replace(path[1..^1], DecodePathEscape);
                }

                if (path.StartsWith("b/", StringComparison.Ordinal))
                {
                    ranges = [];
                    lines[path[2..]] = ranges;
                }
            }
            else if (ranges is not null && line.StartsWith("@@ ", StringComparison.Ordinal))
            {
                var hunk = HunkHeader.Match(line);
                if (hunk.Success && int.TryParse(hunk.Groups[1].Value, out var start))
                {
                    var count = hunk.Groups[2].Success ? int.Parse(hunk.Groups[2].Value) : 1;
                    if (count > 0)
                    {
                        ranges.Add((start, start + count - 1));
                    }
                }
            }
        }

        return lines;
    }

    private static string DecodePathEscape(Match escape) => escape.Groups[1].Value switch
    {
        "a" => "\a",
        "b" => "\b",
        "f" => "\f",
        "n" => "\n",
        "r" => "\r",
        "t" => "\t",
        "v" => "\v",
        "\"" => "\"",
        "\\" => "\\",
        var octal => ((char)Convert.ToInt32(octal, 8)).ToString()
    };

    private static async Task<T> RunGitAsync<T>(string repositoryRoot, string[] arguments, Func<StreamReader, Task<T>> readOutput)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = repositoryRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        var output = readOutput(process.StandardOutput);
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            throw new CliException($"git {arguments[0]} failed (exit {process.ExitCode}): {(await errors).Trim()}");
        }

        return await output;
    }
}
