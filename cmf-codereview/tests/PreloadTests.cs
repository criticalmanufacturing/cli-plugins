using Cmf.Cli.Plugin.CodeReview;
using Xunit;

namespace codereview.tests;

public class PreloadTests
{
    [Theory]
    [InlineData("class Example { Example() { items.LoadReference(\"Material\"); foreach (var item in items) { var value = item.Material.Name; } } }", true)]
    [InlineData("class Example { void Run() { void Local() { items.LoadReference(\"Material\"); foreach (var item in items) { var value = item.Material.Name; } } Local(); } }", true)]
    [InlineData("class Example { void Before() { items.LoadReference(\"Material\"); } void Run() { foreach (var item in items) { var value = item.Material.Name; } } }", false)]
    [InlineData("class Example { void Run() { items.LoadReference(\"Material\"); foreach (var item in items) { void Later() { var value = item.Material.Name; } } } }", false)]
    public void PreloadProofRespectsCallableBoundaries(string source, bool expected)
    {
        var root = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(source).GetRoot();
        var access = root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MemberAccessExpressionSyntax>()
            .Single(a => a.ToString() == "item.Material.Name");
        Assert.Equal(expected, CmPreloadAnalyzer.IsPreloadedBeforeLoop(access));
    }

    [Theory]
    [InlineData("items.LoadReference(\"Material\");", "items", "", false)]
    [InlineData("items.LoadReferences(new[] { \"Material\", \"Product\" });", "items", "", false)]
    [InlineData("items.LoadRelations();", "items", "", false)]
    [InlineData("items.LoadReference(\"Material\");", "items.Where(x => x.Enabled)", "", false)]
    [InlineData("", "items", "", true)]
    [InlineData("items.LoadReference(\"Product\");", "items", "", true)]
    [InlineData("other.LoadReference(\"Material\");", "items", "", true)]
    [InlineData("if (enabled) items.LoadReference(\"Material\");", "items", "", true)]
    [InlineData("items.LoadReference(\"Material\"); items = other;", "items", "", true)]
    [InlineData("items.LoadReference(\"Material\"); var alias = items; Mutate(alias);", "items", "", true)]
    [InlineData("var alias = items; items.LoadReference(\"Material\"); alias.Add(other);", "items", "", true)]
    [InlineData("items.LoadReference(\"Material\"); Mutate(items);", "items", "", true)]
    [InlineData("items.LoadReference(\"Material\");", "items", "item = other;", true)]
    [InlineData("items.LoadReference(\"Material\");", "items", "item.Load();", true)]
    [InlineData("items.LoadReference(\"Material\");", "items.Select(x => new Other())", "", true)]
    public void ScannerOnlySuppressesProvenPreloads(string before, string collection, string inside, bool expected)
    {
        using var fixture = new ActionGroupFixture();
        fixture.Write("Cmf.Sample.Business/cmfpackage.json", """{"packageType":"Business"}""");
        fixture.Write("Cmf.Sample.Business/Cmf.Sample.Orchestration/Example.cs", $$"""
            public class Example {
                public void Run() {
                    {{before}}
                    foreach (var item in {{collection}}) {
                        {{inside}}
                        var value = item.Material.Name;
                    }
                    items.LoadReference("Material");
                }
            }
            """);
        var findings = CmFrameworkScanner.GetFindings(fixture.Root, entityCatalog: TestEntities.Catalog);
        Assert.Equal(expected, findings.Any(f => f.Rule == "ExcessiveLazyLoading"));
    }
}
