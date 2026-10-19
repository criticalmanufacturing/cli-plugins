using Cmf.Cli.Plugin.CodeReview;
using Cmf.Cli.Plugin.CodeReview.Models;
using Cmf.Cli.Plugin.CodeReview.Enums;
using Cmf.CLI.Utilities;
using Xunit;

namespace codereview.tests;

public class CodeReviewPathsTests : IDisposable
{
    private readonly string _tempRoot;

    public CodeReviewPathsTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "codereview-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private static void WriteBusinessManifest(string businessDir)
    {
        Directory.CreateDirectory(businessDir);
        File.WriteAllText(Path.Combine(businessDir, "cmfpackage.json"), """{ "packageType": "Business" }""");
    }

    private static void WriteDataManifestWithDeeContent(string dataDir, string deeSourceGlob = "DEEs/*")
    {
        Directory.CreateDirectory(dataDir);
        File.WriteAllText(Path.Combine(dataDir, "cmfpackage.json"), $$"""
            {
              "packageType": "Data",
              "contentToPack": [
                { "source": "{{deeSourceGlob}}", "target": "DeeRules", "contentType": "DEE" }
              ]
            }
            """);
    }

    /// <summary>Sets up a complete legacy-shaped Cmf.Custom.* repository (both manifests, all subfolders) under <see cref="_tempRoot"/>.</summary>
    private void WriteFullCustomRepository()
    {
        var businessDir = Path.Combine(_tempRoot, "Cmf.Custom.Business");
        WriteBusinessManifest(businessDir);
        Directory.CreateDirectory(Path.Combine(businessDir, "Cmf.Custom.Orchestration"));
        Directory.CreateDirectory(Path.Combine(businessDir, "Cmf.Custom.Common"));
        Directory.CreateDirectory(Path.Combine(businessDir, "Cmf.Custom.Services"));

        var dataDir = Path.Combine(_tempRoot, "Cmf.Custom.Data", "Cmf.Custom.Data.Packages");
        WriteDataManifestWithDeeContent(dataDir);
        Directory.CreateDirectory(Path.Combine(dataDir, "DEEs"));
    }

    [Fact]
    public void ResolveRoots_NoManifestsResolveToBusinessOrData_ThrowsCliException()
    {
        // cmfpackage.json is the only supported project marker -- a repository with none resolving to
        // a Business or Data package isn't a CM Framework customization project this tool can scan, so
        // discovery must fail loudly rather than silently guess at a folder-name convention.
        Assert.Throws<CliException>(() => CodeReviewPaths.ResolveRoots(_tempRoot, ReviewScope.Both));
    }

    [Fact]
    public void ResolveRoots_RepositoryRootDoesNotExist_ThrowsCliException()
    {
        var missingRoot = Path.Combine(_tempRoot, "does-not-exist");

        Assert.Throws<CliException>(() => CodeReviewPaths.ResolveRoots(missingRoot, ReviewScope.Both));
    }

    [Fact]
    public void ResolveRoots_Dees_ResolvesFromDataManifest()
    {
        WriteFullCustomRepository();

        var roots = CodeReviewPaths.ResolveRoots(_tempRoot, ReviewScope.DEEs);

        Assert.Single(roots);
        Assert.Equal(Path.Combine("Cmf.Custom.Data", "Cmf.Custom.Data.Packages", "DEEs"), roots[0]);
    }

    [Fact]
    public void ResolveRoots_Orchestration_ResolvesFromBusinessManifest()
    {
        WriteFullCustomRepository();

        var roots = CodeReviewPaths.ResolveRoots(_tempRoot, ReviewScope.Orchestration);

        Assert.Equal(3, roots.Length);
        Assert.Contains(Path.Combine("Cmf.Custom.Business", "Cmf.Custom.Orchestration"), roots);
        Assert.Contains(Path.Combine("Cmf.Custom.Business", "Cmf.Custom.Common"), roots);
        Assert.Contains(Path.Combine("Cmf.Custom.Business", "Cmf.Custom.Services"), roots);
    }

    [Fact]
    public void ResolveRoots_Both_ReturnsAllFourRoots()
    {
        WriteFullCustomRepository();

        var roots = CodeReviewPaths.ResolveRoots(_tempRoot, ReviewScope.Both);

        Assert.Equal(4, roots.Length);
        Assert.Contains(Path.Combine("Cmf.Custom.Data", "Cmf.Custom.Data.Packages", "DEEs"), roots);
        Assert.Contains(Path.Combine("Cmf.Custom.Business", "Cmf.Custom.Orchestration"), roots);
    }

    [Fact]
    public void ResolveRoots_NonCustomProjectPrefix_DiscoversRootsFromPackageManifests()
    {
        // A customization project using any Cmf.<Project> name -- not just "Custom" -- and the
        // flatter Cmf.<Project>.Data/DEEs shape (no nested *.Data.Packages folder), as seen in
        // SemiconductorTemplate (Cmf.Semi.*). Roots come from the packageType/contentToPack manifest
        // data, not from guessing at folder-name conventions.
        var businessDir = Path.Combine(_tempRoot, "Cmf.Semi.Business");
        WriteBusinessManifest(businessDir);
        Directory.CreateDirectory(Path.Combine(businessDir, "Cmf.Semi.Orchestration"));
        Directory.CreateDirectory(Path.Combine(businessDir, "Cmf.Semi.Common"));
        Directory.CreateDirectory(Path.Combine(businessDir, "Cmf.Semi.Services"));

        var dataDir = Path.Combine(_tempRoot, "Cmf.Semi.Data");
        WriteDataManifestWithDeeContent(dataDir);
        Directory.CreateDirectory(Path.Combine(dataDir, "DEEs"));

        var roots = CodeReviewPaths.ResolveRoots(_tempRoot, ReviewScope.Both);

        Assert.Equal(4, roots.Length);
        Assert.Contains(Path.Combine("Cmf.Semi.Business", "Cmf.Semi.Orchestration"), roots);
        Assert.Contains(Path.Combine("Cmf.Semi.Business", "Cmf.Semi.Common"), roots);
        Assert.Contains(Path.Combine("Cmf.Semi.Business", "Cmf.Semi.Services"), roots);
        Assert.Contains(Path.Combine("Cmf.Semi.Data", "DEEs"), roots);
    }

    [Fact]
    public void ResolveRoots_NonCustomProjectPrefix_NestedDataPackagesShape_IsAlsoDiscovered()
    {
        // The Micron-style nested shape (Cmf.<Project>.Data/Cmf.<Project>.Data.Packages/DEEs) under a
        // non-"Custom" project name -- the DEE root comes from the manifest's own contentToPack source,
        // so both DEEs conventions resolve without any special-casing.
        var packagesDir = Path.Combine(_tempRoot, "Cmf.Foo.Data", "Cmf.Foo.Data.Packages");
        WriteDataManifestWithDeeContent(packagesDir);
        Directory.CreateDirectory(Path.Combine(packagesDir, "DEEs"));

        var roots = CodeReviewPaths.ResolveRoots(_tempRoot, ReviewScope.DEEs);

        Assert.Single(roots);
        Assert.Equal(Path.Combine("Cmf.Foo.Data", "Cmf.Foo.Data.Packages", "DEEs"), roots[0]);
    }

    [Fact]
    public void ResolveRoots_MultipleDataManifestsWithDeeContent_AllContribute()
    {
        // A repository can have more than one Data-typed manifest with DEE content (e.g. a Tests
        // package bundling its own DEE fixtures) -- every one of them contributes a DEE root, not just
        // the first found.
        var mainData = Path.Combine(_tempRoot, "Cmf.Custom.Data", "Cmf.Custom.Data.Packages");
        WriteDataManifestWithDeeContent(mainData);
        Directory.CreateDirectory(Path.Combine(mainData, "DEEs"));

        var testsData = Path.Combine(_tempRoot, "Cmf.Custom.Tests", "MasterData");
        WriteDataManifestWithDeeContent(testsData);
        Directory.CreateDirectory(Path.Combine(testsData, "DEEs"));

        var roots = CodeReviewPaths.ResolveRoots(_tempRoot, ReviewScope.DEEs);

        Assert.Equal(2, roots.Length);
        Assert.Contains(Path.Combine("Cmf.Custom.Data", "Cmf.Custom.Data.Packages", "DEEs"), roots);
        Assert.Contains(Path.Combine("Cmf.Custom.Tests", "MasterData", "DEEs"), roots);
    }

    [Fact]
    public void ResolveRoots_IgnoresBuildOutputAndVcsFoldersWhileSearchingForManifests()
    {
        // A manifest sitting inside bin/obj/.git/node_modules must never be picked up -- these are
        // build output or dependency caches, not real packages, and descending into them at all would
        // also be a needless performance cost on a large repository.
        var mainData = Path.Combine(_tempRoot, "Cmf.Custom.Data", "Cmf.Custom.Data.Packages");
        WriteDataManifestWithDeeContent(mainData);
        Directory.CreateDirectory(Path.Combine(mainData, "DEEs"));

        var decoyDataDir = Path.Combine(_tempRoot, "Cmf.Custom.Business", "bin", "Cmf.Decoy.Data");
        WriteDataManifestWithDeeContent(decoyDataDir);
        Directory.CreateDirectory(Path.Combine(decoyDataDir, "DEEs"));

        var roots = CodeReviewPaths.ResolveRoots(_tempRoot, ReviewScope.DEEs);

        Assert.Single(roots);
        Assert.DoesNotContain(roots, r => r.Contains("Decoy"));
    }

    [Theory]
    [InlineData(ReviewScope.DEEs, "DEEs")]
    [InlineData(ReviewScope.Orchestration, "Orchestration")]
    [InlineData(ReviewScope.Both, "Both")]
    public void GetScopeLabel_RoundTripsResolvedRootsBackToTheirScopeLabel(ReviewScope scope, string expectedLabel)
    {
        WriteFullCustomRepository();
        var roots = CodeReviewPaths.ResolveRoots(_tempRoot, scope);

        var label = CodeReviewPaths.GetScopeLabel(_tempRoot, roots);

        Assert.Equal(expectedLabel, label);
    }

    [Fact]
    public void GetScopeLabel_CustomRootSet_ReturnsCustom()
    {
        WriteFullCustomRepository();

        var label = CodeReviewPaths.GetScopeLabel(_tempRoot, new[] { "SomeArbitraryFolder" });

        Assert.Equal("Custom", label);
    }

    [Fact]
    public void GetFiles_FindsCsFilesUnderResolvedRoots()
    {
        var packagesDir = Path.Combine(_tempRoot, "Cmf.Custom.Data", "Cmf.Custom.Data.Packages");
        WriteDataManifestWithDeeContent(packagesDir);
        var deeDir = Path.Combine(packagesDir, "DEEs");
        Directory.CreateDirectory(deeDir);
        File.WriteAllText(Path.Combine(deeDir, "Sample.cs"), "public class Sample {}");
        File.WriteAllText(Path.Combine(deeDir, "notes.txt"), "not a cs file");

        var files = CodeReviewPaths.GetFiles(_tempRoot, CodeReviewPaths.ResolveRoots(_tempRoot, ReviewScope.DEEs));

        Assert.Single(files);
        Assert.EndsWith("Sample.cs", files[0]);
    }

    [Fact]
    public void GetFiles_ExplicitRootThatDoesNotExistOnDisk_IsSkippedWithoutError()
    {
        // GetFiles itself tolerates a root that doesn't exist (e.g. a stale --roots value), regardless
        // of whether that root came from discovery -- discovered roots are always verified to exist,
        // but an explicitly-passed root might not be.
        var files = CodeReviewPaths.GetFiles(_tempRoot, new[] { Path.Combine("Cmf.Custom.Business", "Cmf.Custom.Orchestration") });

        Assert.Empty(files);
    }

    [Fact]
    public void GetFiles_NestedSubdirectories_AreSearchedRecursively()
    {
        WriteBusinessManifest(Path.Combine(_tempRoot, "Cmf.Custom.Business"));
        var orchestrationDir = Path.Combine(_tempRoot, "Cmf.Custom.Business", "Cmf.Custom.Orchestration", "Sub");
        Directory.CreateDirectory(orchestrationDir);
        File.WriteAllText(Path.Combine(orchestrationDir, "Nested.cs"), "public class Nested {}");

        var files = CodeReviewPaths.GetFiles(_tempRoot, CodeReviewPaths.ResolveRoots(_tempRoot, ReviewScope.Orchestration));

        Assert.Contains(files, f => f.EndsWith("Nested.cs"));
    }

    [Fact]
    public void GetRelativePath_ReturnsPathRelativeToBase()
    {
        var basePath = _tempRoot;
        var fullPath = Path.Combine(_tempRoot, "sub", "file.cs");

        var relative = CodeReviewPaths.GetRelativePath(basePath, fullPath);

        Assert.Equal(Path.Combine("sub", "file.cs"), relative);
    }

    [Theory]
    [InlineData(@"C:\repo\Cmf.Custom.Business\Cmf.Custom.Orchestration\Foo.cs", true)]
    [InlineData(@"C:\repo\Cmf.Semi.Business\Cmf.Semi.Orchestration\Foo.cs", true)]
    [InlineData(@"C:\repo\Cmf.Custom.Data\Cmf.Custom.Data.Packages\DEEs\Foo.cs", false)]
    public void IsOrchestrationFile_MatchesAnyProjectPrefix_ByStructuralShape(string path, bool expected)
    {
        Assert.Equal(expected, CodeReviewPaths.IsOrchestrationFile(path));
    }

    [Theory]
    [InlineData(@"C:\repo\Cmf.Custom.Data\Cmf.Custom.Data.Packages\DEEs\Foo.cs", true)]
    [InlineData(@"C:\repo\Cmf.Semi.Data\DEEs\Foo.cs", true)]
    [InlineData(@"C:\repo\Cmf.Semi.Business\Cmf.Semi.Orchestration\Foo.cs", false)]
    public void IsDeeFile_MatchesBothNestedAndFlatConventions_ForAnyProjectPrefix(string path, bool expected)
    {
        Assert.Equal(expected, CodeReviewPaths.IsDeeFile(path));
    }

    [Theory]
    [InlineData(@"C:\repo\Cmf.Custom.Data\Cmf.Custom.Data.Packages\DEEs\ProcessRules\Foo.cs", true)]
    [InlineData(@"C:\repo\Cmf.Semi.Data\DEEs\ProcessRules\Foo.cs", true)]
    [InlineData(@"C:\repo\Cmf.Semi.Data\DEEs\Foo.cs", false)]
    public void IsOneTimeMigrationFile_MatchesBothConventions_ForAnyProjectPrefix(string path, bool expected)
    {
        Assert.Equal(expected, CodeReviewPaths.IsOneTimeMigrationFile(path));
    }
}
