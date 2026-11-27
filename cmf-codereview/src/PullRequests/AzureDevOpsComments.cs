using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Cmf.CLI.Utilities;
using Cmf.Cli.Plugin.CodeReview.Models;

namespace Cmf.Cli.Plugin.CodeReview.PullRequests;

internal sealed class AzureDevOpsComments(HttpClient client, Uri threadsUri, string token)
{
    private static readonly Regex Marker = new(@"cmf-codereview-id:([0-9a-f]{12})");
    private static readonly JsonSerializerOptions ThreadJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record ThreadList(Thread[] Value);
    private sealed record Thread(Comment[]? Comments);
    private sealed record Comment(string? Content);
    private sealed record Position(int Line, int Offset = 1);
    private sealed record ThreadContext(string FilePath, Position RightFileStart, Position RightFileEnd);

    public static AzureDevOpsComments FromPipeline(HttpClient client)
    {
        var collection = RequiredVariable("SYSTEM_TEAMFOUNDATIONCOLLECTIONURI");
        var project = Uri.EscapeDataString(RequiredVariable("SYSTEM_TEAMPROJECT"));
        var repository = Uri.EscapeDataString(RequiredVariable("BUILD_REPOSITORY_ID"));
        var pr = Uri.EscapeDataString(RequiredVariable("SYSTEM_PULLREQUEST_PULLREQUESTID"));
        var token = RequiredVariable("SYSTEM_ACCESSTOKEN");
        var threadsUri = new Uri($"{collection.TrimEnd('/')}/{project}/_apis/git/repositories/{repository}/pullRequests/{pr}/threads?api-version=7.1");
        return new AzureDevOpsComments(client, threadsUri, token);
    }

    public async Task PublishAsync(IReadOnlyList<Finding> findings, int totalFindings)
    {
        var posted = await GetExistingFingerprintsAsync();
        var severitySummary = string.Join(", ", findings.GroupBy(finding => finding.Severity)
            .OrderByDescending(group => group.Key).Select(group => $"{group.Count()} {group.Key}"));

        // The rendered summary is part of its fingerprint, including for threads from earlier pipeline runs.
        var summary = $"cmf-codereview found {findings.Count} issue(s) on lines changed by this pull request ({severitySummary}). See the inline comments below for details.";
        var skippedCount = totalFindings - findings.Count;
        if (skippedCount > 0)
        {
            summary += $" {skippedCount} additional issue(s) were found elsewhere in the scanned files (flagged due to whole-file cross-reference analysis) but aren't on lines this PR modified, so weren't individually commented -- see the full findings report for those.";
        }

        await PublishThreadAsync(posted, Fingerprint("summary", "", summary), summary);
        foreach (var finding in findings)
        {
            var path = finding.RelativePath.Replace('\\', '/').TrimStart('/');
            var line = finding.LineNumber;
            var body = $"**[{finding.Rule}] {finding.Severity}** -- {path}:{line}\n\n{finding.Description}\n\n**Suggested fix:** {finding.SuggestedFix}";
            var position = new Position(line);
            await PublishThreadAsync(posted, Fingerprint(path, finding.Rule, finding.Snippet), body,
                new ThreadContext($"/{path}", position, position));
        }
    }

    private async Task<HashSet<string>> GetExistingFingerprintsAsync()
    {
        try
        {
            using var request = CreateRequest(HttpMethod.Get);
            using var response = await SendAsync(request);
            var threads = await response.Content.ReadFromJsonAsync<ThreadList>();
            if (threads?.Value is null)
            {
                throw new JsonException("The PR threads response has no value array.");
            }

            return threads.Value.SelectMany(thread => thread.Comments ?? [])
                .SelectMany(comment => Marker.Matches(comment.Content ?? ""))
                .Select(match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is CliException or HttpRequestException or JsonException or TaskCanceledException)
        {
            Console.Error.WriteLine("Warning: could not read existing Azure DevOps PR threads. Continuing without duplicate detection.");
            return [];
        }
    }

    private async Task PublishThreadAsync(HashSet<string> posted, string fingerprint, string body, ThreadContext? context = null)
    {
        if (posted.Contains(fingerprint))
        {
            Console.WriteLine($"Skipping already-posted PR comment ({fingerprint}).");
            return;
        }

        using var request = CreateRequest(HttpMethod.Post);
        request.Content = JsonContent.Create(new
        {
            comments = new[] { new { parentCommentId = 0, content = $"{body}\n\n<!-- cmf-codereview-id:{fingerprint} -->", commentType = 1 } },
            status = "active",
            threadContext = context
        }, options: ThreadJsonOptions);
        using var response = await SendAsync(request);
        posted.Add(fingerprint);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method)
    {
        var request = new HttpRequestMessage(method, threadsUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
    {
        var response = await client.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            response.Dispose();
            throw new CliException($"Azure DevOps PR threads {request.Method} failed (HTTP {status}).");
        }

        return response;
    }

    private static string RequiredVariable(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? throw new CliException($"{name} is required to publish PR comments.") : value;
    }

    private static string Fingerprint(string path, string rule, string snippet) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{path}|{rule}|{snippet}")))[..12].ToLowerInvariant();
}
