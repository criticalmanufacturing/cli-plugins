using Cmf.Cli.Plugin.CodeReview;
using Cmf.Cli.Plugin.CodeReview.Enums;
using Xunit;

namespace codereview.tests;

public class LoadNativeOnlyTests
{
    [Theory]
    [InlineData("entity.Load(); var value = entity.GetNativeValue<long>(\"Id\");", "Medium")]
    [InlineData("var value = entity.GetNativeValue<long>(\"Id\");", null)]
    [InlineData("MissingEntity unknown = GetUnknown(); unknown.Load(); var value = unknown.GetNativeValue<long>(\"Id\");", null)]
    [InlineData("entity.Load();", null)]
    [InlineData("entity.Load(); var value = entity.Name;", null)]
    [InlineData("entity.Name = \"Example\"; entity.Load(); var value = entity.GetNativeValue<long>(\"Id\");", null)]
    [InlineData("entity.Load(); var value = entity.GetNativeValue<long>(\"Id\"); var activities = entity.MaintenanceActivities;", null)]
    [InlineData("other.Load(); var value = entity.GetNativeValue<long>(\"Id\");", null)]
    [InlineData("entity.Load(); var alias = entity; var value = entity.GetNativeValue<long>(\"Id\");", null)]
    [InlineData("var alias = entity; entity.Load(); var value = entity.GetNativeValue<long>(\"Id\");", null)]
    [InlineData("Material alias; alias = entity; entity.Load(); var value = entity.GetNativeValue<long>(\"Id\");", null)]
    [InlineData("var aliases = new[] { entity }; entity.Load(); var value = entity.GetNativeValue<long>(\"Id\");", null)]
    [InlineData("entity.Load(); entity = other; var value = entity.GetNativeValue<long>(\"Id\");", null)]
    [InlineData("entity.Load(); Mutate(entity); var value = entity.GetNativeValue<long>(\"Id\");", null)]
    [InlineData("entity.Load(); void Later() { var value = entity.GetNativeValue<long>(\"Id\"); }", null)]
    [InlineData("foreach (var current in entities) { current.Load(); var value = current.GetNativeValue<long>(\"Id\"); }", "High")]
    [InlineData("foreach (var current in entities) { foreach (var unused in entities) { current.Load(); var value = current.GetNativeValue<long>(\"Id\"); } }", "Critical")]
    [InlineData("holder.Material.Load(); var value = holder.Material.GetNativeValue<long>(\"Id\");", "Medium")]
    [InlineData("holder.Material.Load(); holder = otherHolder; var value = holder.Material.GetNativeValue<long>(\"Id\");", null)]
    [InlineData("holder.Material.Load(); holder.Refresh(); var value = holder.Material.GetNativeValue<long>(\"Id\");", null)]
    [InlineData("for (int i = 0; i < 5; ++i) { entity.Load(); var value = entity.GetNativeValue<long>(\"Id\"); }", "High")]
    public void OnlyBoundedNativeOnlyUsesProduceFindings(string body, string? severity)
    {
        var matches = Scan(body).Where(f => f.Rule == "LoadThenNativeValueOnly").ToList();
        if (severity is null) Assert.Empty(matches);
        else Assert.Equal(Enum.Parse<Severity>(severity), Assert.Single(matches).Severity);
    }

    [Fact]
    public void CallerInducedLoopReusesCriticalWeighting()
    {
        var findings = Scan("foreach (var current in entities) { this.Helper(current); }",
            "void Helper(Material input) { input.Load(); var value = input.GetNativeValue<long>(\"Id\"); }");
        Assert.Equal(Severity.Critical, Assert.Single(findings.Where(f => f.Rule == "LoadThenNativeValueOnly")).Severity);
    }

    private static List<Cmf.Cli.Plugin.CodeReview.Models.Finding> Scan(string body, string extra = "")
    {
        using var fixture = new ActionGroupFixture();
        fixture.Write("Cmf.Sample.Business/cmfpackage.json", """{"packageType":"Business"}""");
        fixture.Write("Cmf.Sample.Business/Example.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>""");
        fixture.Write("Cmf.Sample.Business/Cmf.Sample.Orchestration/Example.cs", $$"""
            using System.Collections.Generic;
            namespace Cmf.Foundation.BusinessObjects {
                public class EntityBase {
                    public void Load() {}
                    public T GetNativeValue<T>(string name) => default(T);
                }
            }
            namespace Example {
                class Material : Cmf.Foundation.BusinessObjects.EntityBase { public string Name { get; set; } public object MaintenanceActivities { get; set; } }
                class Holder { public Material Material { get; set; } public void Refresh() {} }
                class Operations {
                    void Mutate(Material entity) {}
                    void Run(Material entity, Material other, IEnumerable<Material> entities, Holder holder, Holder otherHolder) {
                        {{body}}
                    }
                    {{extra}}
                }
            }
            """);
        return CmFrameworkScanner.GetFindings(fixture.Root, entityCatalog: TestEntities.Catalog);

    }
}
