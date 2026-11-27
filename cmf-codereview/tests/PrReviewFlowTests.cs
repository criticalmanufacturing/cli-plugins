using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Cmf.Cli.Plugin.CodeReview.Commands;
using Cmf.Cli.Plugin.CodeReview.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace codereview.tests;

public class PrReviewFlowTests : IAsyncLifetime
{
    private const string Business = "Cmf.Custom.Business";
    private const string Orchestration = Business + "/Cmf.Custom.Orchestration";
    private const string ChangedFile = Orchestration + "/Example with spaces.cs";
    private const string UnchangedFile = Orchestration + "/Unchanged.cs";
    private const string TargetBranch = "releases/development";
    private const string ThreadsPath = "/Micron Test/_apis/git/repositories/local-repo/pullRequests/42/threads";
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"codereview-pr-{Guid.NewGuid():N}");
    private readonly ConcurrentQueue<JsonElement> _posts = new();
    private WebApplication? _azureDevOps;
    private int _threadReads;
    private string Checkout => Path.Combine(_root, "checkout");
    private string Report => Path.Combine(_root, "findings output.json");

    [Fact]
    public async Task CommentMode_PostsOnlyOnChangedLinesAndSkipsExistingThreads()
    {
        // PR-Changes passes --fail-on even when CmfCodeReviewFailOn is blank.
        var first = await ReviewAsync("");
        Assert.True(first.ExitCode == 0, first.Output);
        AssertReportScope();

        Assert.Equal(1, _threadReads);
        Assert.Collection(_posts,
            summary =>
            {
                Assert.False(summary.TryGetProperty("threadContext", out _));
                Assert.Contains("1 issue(s) on lines changed", CommentBody(summary));
            },
            inline =>
            {
                var context = inline.GetProperty("threadContext");
                Assert.Equal("/" + ChangedFile, context.GetProperty("filePath").GetString());
                Assert.Equal(11, context.GetProperty("rightFileStart").GetProperty("line").GetInt32());
                Assert.Equal(11, context.GetProperty("rightFileEnd").GetProperty("line").GetInt32());
                Assert.Contains("[LoadInsideLoop] High", CommentBody(inline));
            });

        var retry = await ReviewAsync("");
        Assert.True(retry.ExitCode == 0, retry.Output);
        Assert.Equal(2, _threadReads);
        Assert.Equal(2, _posts.Count);
    }

    [Fact]
    public async Task SeverityGate_FailsAfterWritingFindingsAndBeforeContactingAzureDevOps()
    {
        var result = await ReviewAsync("High");

        Assert.True(result.ExitCode == 1, result.Output);
        Assert.Contains("findings at or above High", result.Output);
        AssertReportScope();
        Assert.Equal(0, _threadReads);
        Assert.Empty(_posts);
    }

    public async Task InitializeAsync()
    {
        var orchestration = Path.Combine(Checkout, Orchestration);
        Directory.CreateDirectory(orchestration);
        await GitAsync("init", "-b", TargetBranch);
        await GitAsync("config", "user.name", "PR review test");
        await GitAsync("config", "user.email", "pr-review@example.invalid");
        await GitAsync("config", "core.hooksPath", Path.Combine(_root, "no-hooks"));
        await GitAsync("config", "commit.gpgsign", "false");

        await File.WriteAllTextAsync(Path.Combine(Checkout, Business, "cmfpackage.json"), """{"packageType":"Business"}""");
        await File.WriteAllTextAsync(Path.Combine(Checkout, ChangedFile), Source("Example", "otherEntity.Prepare();"));
        await File.WriteAllTextAsync(Path.Combine(Checkout, UnchangedFile), Source("Unchanged", "otherEntity.Load();"));
        await GitAsync("add", ".");
        await GitAsync("commit", "-m", "Target branch");
        var origin = Path.Combine(_root, "origin.git");
        await GitAsync("clone", "--bare", "--local", "--no-hardlinks", ".", origin);
        await GitAsync("remote", "add", "origin", origin);
        await GitAsync("switch", "-c", "feature/review");
        await File.WriteAllTextAsync(Path.Combine(Checkout, ChangedFile), Source("Example", "otherEntity.Load();"));
        await GitAsync("add", ".");
        await GitAsync("commit", "-m", "Introduce one finding");

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        _azureDevOps = builder.Build();
        _azureDevOps.Urls.Add("http://127.0.0.1:0");
        _azureDevOps.MapMethods(ThreadsPath, ["GET", "POST"], (Delegate)HandleThreadsAsync);
        await _azureDevOps.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_azureDevOps is not null)
        {
            await _azureDevOps.DisposeAsync();
        }

        if (OperatingSystem.IsWindows())
        {
            foreach (var file in new DirectoryInfo(_root).EnumerateFiles("*",
                new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
            {
                if (file.IsReadOnly)
                {
                    file.IsReadOnly = false;
                }
            }
        }

        Directory.Delete(_root, recursive: true);
    }

    private async Task<IResult> HandleThreadsAsync(HttpContext context)
    {
        if (context.Request.Headers.Authorization != "Bearer mock-token" || context.Request.Query["api-version"] != "7.1")
        {
            return Results.BadRequest();
        }

        if (HttpMethods.IsGet(context.Request.Method))
        {
            Interlocked.Increment(ref _threadReads);
            return Results.Json(new { value = _posts.ToArray() });
        }

        using var request = await JsonDocument.ParseAsync(context.Request.Body);
        _posts.Enqueue(request.RootElement.Clone());
        return Results.StatusCode(StatusCodes.Status201Created);
    }

    private Task<(int ExitCode, string Output)> ReviewAsync(string failOn) =>
        RunAsync("dotnet", ["exec",
            "--runtimeconfig", Path.ChangeExtension(typeof(PrReviewFlowTests).Assembly.Location, ".runtimeconfig.json"),
            "--depsfile", Path.ChangeExtension(typeof(PrReviewFlowTests).Assembly.Location, ".deps.json"),
            typeof(PrReviewCommand).Assembly.Location, "pr-review",
            "--repository-root", Checkout, "--output", Report, "--fail-on", failOn], new()
        {
            ["SYSTEM_PULLREQUEST_TARGETBRANCH"] = $"refs/heads/{TargetBranch}",
            ["SYSTEM_TEAMFOUNDATIONCOLLECTIONURI"] = _azureDevOps!.Urls.Single() + "/",
            ["SYSTEM_TEAMPROJECT"] = "Micron Test",
            ["BUILD_REPOSITORY_ID"] = "local-repo",
            ["SYSTEM_PULLREQUEST_PULLREQUESTID"] = "42",
            ["SYSTEM_ACCESSTOKEN"] = "mock-token",
            ["NO_PROXY"] = "127.0.0.1",
            ["cmf_codereview_enable_telemetry"] = "0"
        });

    private async Task GitAsync(params string[] arguments)
    {
        var result = await RunAsync("git", arguments);
        Assert.True(result.ExitCode == 0, $"git {string.Join(' ', arguments)}{Environment.NewLine}{result.Output}");
    }

    private async Task<(int ExitCode, string Output)> RunAsync(string executable, string[] arguments,
        Dictionary<string, string>? environment = null)
    {
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = Checkout,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }
        foreach (var (name, value) in environment ?? [])
        {
            info.Environment[name] = value;
        }

        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException($"{executable} did not exit within 30 seconds.");
        }

        return (process.ExitCode, await stdout + Environment.NewLine + await stderr);
    }

    private void AssertReportScope()
    {
        var findings = JsonSerializer.Deserialize<List<Finding>>(File.ReadAllText(Report))!;
        Assert.All(findings, finding => Assert.Equal(ChangedFile, finding.RelativePath.Replace('\\', '/')));
        Assert.Equal(new[] { 7, 11 }, findings.Where(finding => finding.Rule == "LoadInsideLoop").Select(finding => finding.LineNumber));
    }

    private static string? CommentBody(JsonElement thread) =>
        thread.GetProperty("comments")[0].GetProperty("content").GetString();

    private static string Source(string type, string changedLine) => $$"""
        public class {{type}}
        {
            public void DoWork()
            {
                foreach (var item in items)
                {
                    entity.Load();
                }
                foreach (var item in otherItems)
                {
                    {{changedLine}}
                }
            }
        }
        """;
}
