using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
// Opt-in compilation check against the platform version restored by a customization project.
// Product assemblies are loaded only in this helper, never by the reviewer's scan.
if (args.Length is < 1 or > 2)
{
    Console.Error.WriteLine("Usage: DeeCompilerCheck <DEE-project.csproj> [results.json]");
    return 2;
}
var projectPath = Path.GetFullPath(args[0]);
if (!File.Exists(projectPath)) throw new FileNotFoundException("DEE project not found", projectPath);
var assetsPath = Path.Combine(Path.GetDirectoryName(projectPath)!, "obj", "project.assets.json");
if (!File.Exists(assetsPath)) throw new FileNotFoundException("Restore the DEE project before running this check", assetsPath);
var directory = Path.Combine(Path.GetTempPath(), "dee-compiler-check-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
var paths = Directory.GetFiles(RuntimeEnvironment.GetRuntimeDirectory(), "*.dll").ToDictionary(p => Path.GetFileName(p)!, p => p, StringComparer.OrdinalIgnoreCase);
using var assets = JsonDocument.Parse(File.ReadAllText(assetsPath));
var root = assets.RootElement;
var target = root.GetProperty("targets").EnumerateObject().FirstOrDefault(t => !t.Name.Contains('/'));
if (target.Value.ValueKind == JsonValueKind.Undefined) target = root.GetProperty("targets").EnumerateObject().First();
foreach (var lib in target.Value.EnumerateObject())
{
    var info = root.GetProperty("libraries").GetProperty(lib.Name);
    if (info.GetProperty("type").GetString() != "package") continue;
    var group = lib.Value.TryGetProperty("runtime", out var runtime) ? runtime : lib.Value.TryGetProperty("compile", out var compile) ? compile : default;
    if (group.ValueKind != JsonValueKind.Object) continue;
    foreach (var asset in group.EnumerateObject().Where(a => a.Name.EndsWith(".dll")))
        foreach (var folder in root.GetProperty("packageFolders").EnumerateObject())
        {
            var path = Path.Combine(folder.Name, info.GetProperty("path").GetString()!, asset.Name.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path)) paths.TryAdd(Path.GetFileName(path), path);
        }
}
AssemblyLoadContext.Default.Resolving += (_, name) => paths.TryGetValue(name.Name + ".dll", out var path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
var common = AssemblyLoadContext.Default.LoadFromAssemblyPath(paths["Cmf.Foundation.Common.dll"]);
var compiler = common.GetType("Cmf.Foundation.Common.DynamicExecutionEngine.Compiler", true)!;
var utilities = common.GetType("Cmf.Foundation.Common.Utilities", true)!;
var appVersion = utilities.GetProperty("AppVersion")!;
var version = Activator.CreateInstance(appVersion.PropertyType)!;
appVersion.PropertyType.GetProperty("IsOnlyCoreRunning")!.SetValue(version, false);
appVersion.SetValue(null, version);
var allRuntimeReferences = Directory.GetFiles(RuntimeEnvironment.GetRuntimeDirectory(), "*.dll").Where(p => { using var stream = File.OpenRead(p); using var pe = new System.Reflection.PortableExecutable.PEReader(stream); return pe.HasMetadata; }).Select(p => MetadataReference.CreateFromFile(p)).ToArray();
var customPath = Path.Combine(directory, "Custom.Library.dll");
var emitted = CSharpCompilation.Create("Custom.Library", [CSharpSyntaxTree.ParseText("namespace Custom.Tools { public class Widget { } }")], allRuntimeReferences, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)).Emit(customPath);
if (!emitted.Success) throw new Exception(string.Join(";", emitted.Diagnostics));
paths["Custom.Library.dll"] = customPath;
const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
var generate = compiler.GetMethod("GenerateFullActionCode", flags, null, [typeof(string), typeof(string), typeof(string)], null)!;
var getReferences = compiler.GetMethod("GetReferences", flags)!;
var compileSources = compiler.GetMethods(flags).Single(m => m.Name == "CompileSources" && m.GetParameters()[2].ParameterType == typeof(CSharpCompilationOptions));
var cases = new[] {
    (Name: "missing-assembly", Code: "UseReference(\"\", \"Custom.Tools\"); new Widget();", Expected: false),
    (Name: "missing-namespace", Code: "UseReference(\"Custom.Library.dll\", \"\"); new Widget();", Expected: false),
    (Name: "valid", Code: "UseReference(\"Custom.Library.dll\", \"Custom.Tools\"); new Widget();", Expected: true),
    (Name: "independent-inputs", Code: "UseReference(\"System.Console.dll\", \"Custom.Tools\"); UseReference(\"Custom.Library.dll\", \"\"); new Widget();", Expected: true),
    (Name: "empty-no-op", Code: "UseReference(\"\", \"\");", Expected: true)
};
var checks = new List<object>();
foreach (var item in cases)
{
    var source = (string)generate.Invoke(null, [item.Name, item.Code, "return true;"])!;
    var referenceNames = (IEnumerable<string>)getReferences.Invoke(null, [item.Code + "return true;"])!;
    var references = referenceNames.Select(p => paths.TryGetValue(Path.GetFileName(p), out var resolved) ? resolved : p).Distinct().ToArray();
    var result = (EmitResult)compileSources.Invoke(null, [new[] { source }, references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary), directory, item.Name + ".dll"])!;
    var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.ToString()).ToArray();
    checks.Add(new { item.Name, item.Expected, result.Success, Errors = errors });
    if (result.Success != item.Expected) throw new Exception(JsonSerializer.Serialize(checks));
}
var json = JsonSerializer.Serialize(new { ProductAssembly = common.Location, Version = common.GetName().Version!.ToString(), ArtifactDirectory = directory, Checks = checks }, new JsonSerializerOptions { WriteIndented = true });
if (args.Length == 2)
{
    var output = Path.GetFullPath(args[1]);
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    File.WriteAllText(output, json);
}
Console.WriteLine(json);
return 0;
