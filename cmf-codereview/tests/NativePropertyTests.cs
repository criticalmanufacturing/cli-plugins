using Cmf.Cli.Plugin.CodeReview;
using Xunit;

namespace codereview.tests;

public class NativePropertyTests
{
    [Theory]
    [InlineData("Material", "string", "Name", false, true)]
    [InlineData("Product", "string", "Name", false, true)]
    [InlineData("Resource", "string", "Name", false, true)]
    [InlineData("Material", "long", "Name", false, false)]
    [InlineData("Material", "string", "DynamicAttribute", false, false)]
    [InlineData("Material", "long", "Product", false, false)]
    [InlineData("OtherType", "string", "Name", false, false)]
    [InlineData("Material", "string", "Name", true, false)]
    public void OnlyVerifiedScalarWrappersAreRecommended(string type, string valueType, string attribute, bool differentGetter, bool expected)
    {
        using var fixture = new ActionGroupFixture();
        fixture.Write("Cmf.Sample.Business/cmfpackage.json", """{"packageType":"Business"}""");
        fixture.Write("Cmf.Sample.Business/Example.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>""");
        var getter = differentGetter ? "public new string Name => Other.Name; public Material Other { get; set; }" : "";
        fixture.Write("Cmf.Sample.Business/Cmf.Sample.Orchestration/Example.cs", $$"""
            namespace Cmf.Foundation.BusinessObjects {
                public class EntityBase {
                    public T GetNativeValue<T>(string name) => default(T);
                    public string Name => GetNativeValue<string>("Name");
                }
            }
            namespace Cmf.Navigo.BusinessObjects {
                public class Material : Cmf.Foundation.BusinessObjects.EntityBase { {{getter}} }
                public class Product : Cmf.Foundation.BusinessObjects.EntityBase {}
                public class Resource : Cmf.Foundation.BusinessObjects.EntityBase {}
                public class OtherType : Cmf.Foundation.BusinessObjects.EntityBase {}
            }
            namespace Example {
                public class Operations {
                    public void Run(Cmf.Navigo.BusinessObjects.{{type}} entity) {
                        var value = entity.GetNativeValue<{{valueType}}>("{{attribute}}");
                    }
                }
            }
            """);
        var findings = CmFrameworkScanner.GetFindings(fixture.Root, entityCatalog: TestEntities.Catalog);
        Assert.Equal(expected, findings.Any(f => f.Rule == "NativeValueInsteadOfProperty"));
    }
}
