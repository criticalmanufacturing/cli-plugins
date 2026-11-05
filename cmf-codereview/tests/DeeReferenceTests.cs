using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Reflection.PortableExecutable;
using Cmf.Cli.Plugin.CodeReview;
using Cmf.Cli.Plugin.CodeReview.Rules;
using Cmf.Cli.Plugin.CodeReview.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace codereview.tests;

public sealed class DeeReferenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dee-references-" + Guid.NewGuid());
    private readonly string _project;
    private readonly string _file;
    private readonly CodeReviewFileCache _cache = new();

    public DeeReferenceTests()
    {
        _project = Path.Combine(_root, "Cmf.Test.Data", "DEEs");
        Directory.CreateDirectory(_project);
        _file = Path.Combine(_project, "Example.cs");
        Emit("Cmf.Foundation.Common", """
            namespace Cmf.Foundation.Common
            {
                public class DeeDevBase
                {
                    protected void UseReference(string assembly, string ns) { }
                    public virtual void DeeActionCode() { }
                    public virtual bool DeeTestCondition() => true;
                }
            }
            namespace Cmf.Foundation.Common.DynamicExecutionEngine
            {
                public static class Compiler
                {
                    public static readonly string[] CoreNamespaces = new[] { "System", "System.Collections.Generic", "Cmf.Foundation.Common" };
                    public static readonly string[] MesNamespaces = new string[0];
                    public static readonly string[] CoreReferences = new[] { "System.Private.CoreLib.dll", "System.Console.dll", "Cmf.Foundation.Common.dll" };
                    public static readonly string[] MesReferences = new string[0];
                    public static readonly string[] RuntimeReferences = new string[0];
                    static Compiler() { throw new System.InvalidOperationException("This code must never execute during analysis"); }
                }
            }
            """);
        Emit("Custom.Library", """
            namespace Custom.Tools { public class Widget { } }
            namespace Custom.Extensions { public static class Extensions { public static int Extra(this string value) => value.Length; } }
            """);
        File.WriteAllText(Path.Combine(_project, "Dees.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
            <ItemGroup>
              <Reference Include="Cmf.Foundation.Common"><HintPath>Cmf.Foundation.Common.dll</HintPath></Reference>
              <Reference Include="Custom.Library"><HintPath>Custom.Library.dll</HintPath></Reference>
            </ItemGroup></Project>
            """);
    }

    private void Emit(string name, string source)
    {
        var refs = Directory.GetFiles(RuntimeEnvironment.GetRuntimeDirectory(), "*.dll").Where(p => { using var stream = File.OpenRead(p); using var pe = new PEReader(stream); return pe.HasMetadata; }).Select(p => MetadataReference.CreateFromFile(p));
        var compilation = CSharpCompilation.Create(name, [CSharpSyntaxTree.ParseText(source)], refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var result = compilation.Emit(Path.Combine(_project, name + ".dll"));
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
    }

    private List<RuleMatch> Review(string body, string imports = "using Custom.Tools;")
    {
        File.WriteAllText(_file, $$"""
            using Cmf.Foundation.Common;
            using System;
            {{imports}}
            public class Example : DeeDevBase
            {
                public override void DeeActionCode()
                {
                    {{body}}
                }
            }
            """);
        var context = RuleFileContext.Build(_cache.Get(_file), new(), new ConcurrentDictionary<string, LoopExposure?>(), EntityCatalog.Empty);
        return new DeeReferenceRule(new DeeReferenceRepository(_root, _cache)).FindMatches(context).ToList();
    }

    [Fact]
    public void ValidExplicitReference_ResolvesNamespaceAndAssembly() =>
        Assert.Empty(Review("UseReference(\"Custom.Library.dll\", \"Custom.Tools\"); var value = new Widget();"));

    [Fact]
    public void UsedNamespaceMissingItsReference_IsFlaggedAtTheUse() =>
        Assert.Contains(Review("var value = new Widget();"), f => f.RuleName == "DeeMissingReference" && f.Severity == Cmf.Cli.Plugin.CodeReview.Enums.Severity.High);

    [Fact]
    public void UnusedUsingDoesNotRequireAReference() => Assert.Empty(Review("Console.WriteLine(1);"));

    [Fact]
    public void NamespaceAbsentFromAllSelectedAssemblies_IsInvalid() =>
        Assert.Contains(Review("UseReference(\"System.Private.CoreLib.dll\", \"Custom.Tools\");"), f => f.RuleName == "DeeInvalidReference");

    [Fact]
    public void NamespaceCanBeSuppliedByADifferentDefaultAssembly() =>
        Assert.Empty(Review("UseReference(\"Custom.Library.dll\", \"System\"); new Custom.Tools.Widget();", ""));

    [Fact]
    public void NamespaceCanBeSuppliedByAnotherDeclaredAssembly() =>
        Assert.Empty(Review("UseReference(\"System.Console.dll\", \"Custom.Tools\"); UseReference(\"Custom.Library.dll\", \"\"); new Widget();"));

    [Theory]
    [InlineData("System.Runtime", "namespace Legacy { public class Placeholder { } }")]
    [InlineData("System.Collections", "namespace System.Collections.Generic { public class Dictionary<TKey, TValue> { } }")]
    public void LegacyPackageFrameworkReferencesDoNotReplaceRuntimeAssemblies(string assembly, string source)
    {
        Emit(assembly, "[assembly: System.Reflection.AssemblyVersion(\"4.0.0.0\")]\n" + source);
        var project = Path.Combine(_project, "Dees.csproj");
        File.WriteAllText(project, File.ReadAllText(project).Replace("</Project>",
            $"<ItemGroup><Reference Include=\"{assembly}\"><HintPath>{assembly}.dll</HintPath></Reference></ItemGroup></Project>"));
        Assert.Empty(Review("Console.WriteLine(new Dictionary<string, object>().Count);", "using System.Collections.Generic;"));
    }

    [Fact]
    public void UnknownAssembly_IsUnresolvedRatherThanProvenInvalid() =>
        Assert.Contains(Review("UseReference(\"Unavailable.dll\", \"Custom.Tools\");"), f => f.RuleName == "DeeReferenceUnresolved");

    [Fact]
    public void EmptyAssemblyForFrameworkNamespace_IsValid() =>
        Assert.Empty(Review("UseReference(\"\", \"System.Threading\"); Thread.Sleep(0);", "using System.Threading;"));

    [Fact]
    public void EmptyAssemblyForCustomNamespace_RequiresAnExplicitAssembly() =>
        Assert.Contains(Review("UseReference(\"\", \"Custom.Tools\"); new Widget();"), f => f.RuleName == "DeeInvalidReference");

    [Fact]
    public void AssemblyOnlyReferenceWithFullyQualifiedType_IsValid() =>
        Assert.Empty(Review("UseReference(\"Custom.Library.dll\", \"\"); new Custom.Tools.Widget();", ""));

    [Fact]
    public void MissingReferenceForExtensionImport_IsFlagged() =>
        Assert.Contains(Review("var length = \"text\".Extra();", "using Custom.Extensions;"), f => f.RuleName == "DeeMissingReference");

    [Fact]
    public void DynamicDeclaration_IsUnresolvedAndDoesNotInventMissingReferences()
    {
        var findings = Review("var ns = \"Custom.Tools\"; UseReference(\"Custom.Library.dll\", ns); new Widget();");
        Assert.Contains(findings, f => f.RuleName == "DeeReferenceUnresolved");
        Assert.DoesNotContain(findings, f => f.RuleName == "DeeMissingReference");
    }

    [Fact]
    public void RuntimeDefaultsAreReadWithoutExecutingProductCode()
    {
        Review("Console.WriteLine(1);");
        var project = new DeeReferenceRepository(_root, _cache).ForFile(_file)!;
        Assert.True(project.DefaultsAvailable);
        Assert.Contains("System", project.DefaultNamespaces);
        Assert.Contains("System.Private.CoreLib", project.DefaultAssemblies);
    }

    [Fact]
    public void MissingOwningProject_ProducesValidationLimitation()
    {
        File.Delete(Path.Combine(_project, "Dees.csproj"));
        Assert.Contains(Review("Console.WriteLine(1);"), f => f.RuleName == "DeeReferenceUnresolved");
    }

    [Fact]
    public void EmptyDeclaration_IsANoOp() => Assert.Empty(Review("UseReference(\"\", \"\");"));

    [Fact]
    public void EmptyAssemblyNamespaceCanUseAReferenceDeclaredLater()
    {
        Assert.Empty(Review("UseReference(\"\", \"Custom.Tools\"); UseReference(\"Custom.Library.dll\", \"\"); new Widget();"));
    }

    [Fact]
    public void ReferencedProjectNamespaceCanBeValidatedBeforeItsDllIsBuilt()
    {
        var common = Path.Combine(_root, "Common");
        Directory.CreateDirectory(common);
        File.WriteAllText(Path.Combine(common, "Common.csproj"), """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><AssemblyName>Project.Common</AssemblyName><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>""");
        File.WriteAllText(Path.Combine(common, "Widget.cs"), "namespace Project.Tools { public class SourceWidget { } }");
        var project = Path.Combine(_project, "Dees.csproj");
        File.WriteAllText(project, File.ReadAllText(project).Replace("</Project>", "<ItemGroup><ProjectReference Include=\"../../Common/Common.csproj\" /></ItemGroup></Project>"));
        Assert.Empty(Review("UseReference(\"Project.Common.dll\", \"Project.Tools\"); new SourceWidget();", "using Project.Tools;"));
    }

    [Fact]
    public void UnboundTypeIsReportedAsAValidationLimitation()
    {
        Assert.Contains(Review("UnknownType value;"), f => f.RuleName == "DeeReferenceUnresolved");
    }

    [Fact]
    public void AnonymousTypesDoNotInventGlobalNamespaceImports() =>
        Assert.Empty(Review("var item = new { Name = \"value\" }; Console.WriteLine(item.Name);"));

    public void Dispose() => Directory.Delete(_root, true);
}