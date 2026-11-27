using Cmf.Cli.Plugin.CodeReview;
using Xunit;

namespace codereview.tests;

public class NonEntityTests
{
    [Theory]
    [InlineData("class MaterialDto { public string Name { get; set; } }", true, false, false)]
    [InlineData("class MaterialDto { public string Name { get; set; } }", true, true, false)]
    [InlineData("class MaterialDto : EntityBase { public string Name { get; set; } }", true, false, true)]
    [InlineData("class MaterialDto { public string Name { get; set; } public void LoadReference(string name) {} }", true, false, true)]
    [InlineData("class MaterialDto : MissingBase { public string Name { get; set; } }", true, false, true)]
    [InlineData("", true, false, true)]
    [InlineData("class MaterialDto { public string Name { get; set; } }", false, false, true)]
    public void OnlyResolvedDtosWithoutLazyApisAreSuppressed(string declaration, bool project, bool linq, bool expected)
    {
        using var fixture = new ActionGroupFixture();
        fixture.Write("Cmf.Sample.Business/cmfpackage.json", """{"packageType":"Business"}""");
        if (project) fixture.Write("Cmf.Sample.Business/Example.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>""");
        var body = linq ? "var result = records.Select(record => record.Material.Name);" : "foreach (var record in records) { var value = record.Material.Name; }";
        fixture.Write("Cmf.Sample.Business/Cmf.Sample.Orchestration/Example.cs", $$"""
            using System.Collections.Generic;
            using System.Linq;
            class EntityBase {}
            {{declaration}}
            class Record { public MaterialDto Material { get; set; } }
            class Example {
                void Run(IEnumerable<Record> records) {
                    {{body}}
                }
            }
            """);
        var findings = CmFrameworkScanner.GetFindings(fixture.Root, entityCatalog: TestEntities.Catalog);
        Assert.Equal(expected, findings.Any(f => f.Rule == "ExcessiveLazyLoading"));
    }
}
