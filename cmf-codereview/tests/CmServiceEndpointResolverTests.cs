using Cmf.Cli.Plugin.CodeReview;
using Xunit;

namespace codereview.tests;

public class CmServiceEndpointResolverTests
{
    [Fact]
    public void JoinRoute_NullOrWhitespaceSegment_ReturnsBaseRoute()
    {
        Assert.Equal("api/foo", CmServiceEndpointResolver.JoinRoute("api/foo", null));
        Assert.Equal("api/foo", CmServiceEndpointResolver.JoinRoute("api/foo", ""));
        Assert.Equal("api/foo", CmServiceEndpointResolver.JoinRoute("api/foo", "   "));
    }

    [Fact]
    public void JoinRoute_SegmentStartingWithSlash_IsTreatedAsAbsoluteAndReplacesBase()
    {
        Assert.Equal("bar/baz", CmServiceEndpointResolver.JoinRoute("api/foo", "/bar/baz"));
    }

    [Fact]
    public void JoinRoute_RelativeSegment_IsAppendedToBaseWithSingleSlash()
    {
        Assert.Equal("api/foo/bar", CmServiceEndpointResolver.JoinRoute("api/foo", "bar"));
    }

    [Fact]
    public void JoinRoute_BaseWithTrailingSlashAndSegmentWithLeadingSlash_AvoidsDoubleSlash()
    {
        // Segment starting with '/' is absolute per JoinRoute's contract, base is ignored.
        Assert.Equal("bar", CmServiceEndpointResolver.JoinRoute("api/foo/", "/bar"));
    }

    [Fact]
    public void JoinRoute_TrimsLeadingAndTrailingSlashesFromResult()
    {
        Assert.Equal("api/foo/bar", CmServiceEndpointResolver.JoinRoute("/api/foo/", "bar/"));
    }

    [Fact]
    public void JoinRoute_BackslashesInResult_AreNormalizedToForwardSlashes()
    {
        var result = CmServiceEndpointResolver.JoinRoute(@"api\foo", "bar");
        Assert.DoesNotContain('\\', result);
    }

    [Fact]
    public void GetServiceEndpointMap_MissingControllerFile_ReturnsEmptyMap()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "codereview-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(tempRoot);
        try
        {
            var map = CmServiceEndpointResolver.GetServiceEndpointMap(tempRoot);
            Assert.Empty(map);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData("Project")]
    [InlineData("Other")]
    public void GetServiceEndpointMap_SyntheticController_ResolvesVerbAndRoute_RegardlessOfProjectControllerName(string projectName)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "codereview-tests-" + Guid.NewGuid());
        var controllerDir = Path.Combine(tempRoot, "Cmf.Custom.Business", "Cmf.Custom.Services");
        Directory.CreateDirectory(controllerDir);
        try
        {
            var controllerContent = $$"""
                [Route("api/[controller]/[action]")]
                public class {{projectName}}Controller
                {
                    [HttpGet]
                    public string GetItem()
                    {
                        return "ok";
                    }

                    [HttpPost]
                    [Route("/custom/route")]
                    public string CreateItem()
                    {
                        return "ok";
                    }
                }
                """;

            File.WriteAllText(Path.Combine(controllerDir, $"{projectName}Controller.cs"), controllerContent);

            var map = CmServiceEndpointResolver.GetServiceEndpointMap(tempRoot);

            Assert.True(map.ContainsKey("GetItem"));
            Assert.Equal($"GET /api/{projectName}/GetItem", map["GetItem"].Endpoint);

            Assert.True(map.ContainsKey("CreateItem"));
            Assert.Equal("POST /custom/route", map["CreateItem"].Endpoint);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void GetServiceEndpointMap_MultipleAttributesOnOneBracketedLine_AreBothRecognized()
    {
        // Regression test: the previous per-line regex required exactly one attribute per line
        // (`^\[Http...\]$`/`^\[Route...\]$` anchored to the whole trimmed line), so
        // `[HttpPost, Route("...")]` on a single line would match neither. Attributes are now
        // read from the syntax tree's attribute lists, which handles any grouping/spacing.
        var tempRoot = Path.Combine(Path.GetTempPath(), "codereview-tests-" + Guid.NewGuid());
        var controllerDir = Path.Combine(tempRoot, "Cmf.Custom.Business", "Cmf.Custom.Services");
        Directory.CreateDirectory(controllerDir);
        try
        {
            var controllerContent = """
                public class ProjectController
                {
                    [HttpPost, Route("/grouped/route")]
                    public string GroupedAction()
                    {
                        return "ok";
                    }
                }
                """;

            File.WriteAllText(Path.Combine(controllerDir, "ProjectController.cs"), controllerContent);

            var map = CmServiceEndpointResolver.GetServiceEndpointMap(tempRoot);

            Assert.True(map.ContainsKey("GroupedAction"));
            Assert.Equal("POST /grouped/route", map["GroupedAction"].Endpoint);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }
}
