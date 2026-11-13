using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Xml.Linq;
using Cmf.Cli.Plugin.CodeReview.Rules;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Cmf.Cli.Plugin.CodeReview;

/// <summary>Project-scoped compiler metadata. Referenced assemblies are read, never executed.</summary>
public sealed class DeeReferenceRepository(string repositoryRoot, CodeReviewFileCache cache)
{
    private readonly ConcurrentDictionary<string, Lazy<DeeReferenceProject>> _projects = new(StringComparer.OrdinalIgnoreCase);

    public DeeReferenceProject? ForFile(string file)
    {
        var root = Path.GetFullPath(repositoryRoot);
        for (var directory = new DirectoryInfo(Path.GetDirectoryName(file)!); directory is not null; directory = directory.Parent)
        {
            var projects = directory.GetFiles("*.csproj");
            // Do not guess which of multiple projects owns this file.
            if (projects.Length > 1) return null;
            if (projects.Length == 1)
                return _projects.GetOrAdd(projects[0].FullName, p => new Lazy<DeeReferenceProject>(() => Build(p))).Value;
            if (directory.FullName.Equals(root, StringComparison.OrdinalIgnoreCase)) break;
        }
        return null;
    }

    private DeeReferenceProject Build(string project)
    {
        var paths = Directory.GetFiles(RuntimeEnvironment.GetRuntimeDirectory(), "*.dll")
            .ToDictionary(p => Path.GetFileName(p)!, p => p, StringComparer.OrdinalIgnoreCase);
        // The DEE compiler takes framework assemblies from the host runtime. NuGet's older
        // System.* reference assets must not replace them and create conflicting identities.
        var runtimeFiles = paths.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var trees = new Dictionary<string, SyntaxTree>(StringComparer.OrdinalIgnoreCase);
        var sourceAssemblies = new Dictionary<SyntaxTree, string>();
        var sourceNamespaces = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var metadataAvailable = false;
        var complete = true;
        void AddReference(string path)
        {
            if (!runtimeFiles.Contains(Path.GetFileName(path))) paths[Path.GetFileName(path)] = path;
            metadataAvailable = true;
        }
        void Visit(string path)
        {
            path = Path.GetFullPath(path);
            if (!visited.Add(path)) return;
            var directory = Path.GetDirectoryName(path)!;
            try
            {
                var xml = XDocument.Load(path);
                var assemblyName = xml.Descendants().FirstOrDefault(e => e.Name.LocalName == "AssemblyName")?.Value ?? Path.GetFileNameWithoutExtension(path);
                var namespaces = new HashSet<string>(StringComparer.Ordinal);
                sourceNamespaces[assemblyName] = namespaces;
                foreach (var file in SourceFiles(directory))
                {
                    var syntax = cache.Get(file);
                    trees.TryAdd(file, syntax.Root.SyntaxTree);
                    sourceAssemblies.TryAdd(syntax.Root.SyntaxTree, assemblyName);
                    foreach (var ns in syntax.Root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.BaseNamespaceDeclarationSyntax>())
                        namespaces.Add(string.Join(".", ns.Ancestors().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.BaseNamespaceDeclarationSyntax>().Reverse().Select(n => n.Name.ToString()).Append(ns.Name.ToString())));
                }
                var assets = Path.Combine(directory, "obj", "project.assets.json");
                if (File.Exists(assets))
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(assets));
                    var json = document.RootElement;
                    if (json.TryGetProperty("targets", out var targets) && json.TryGetProperty("libraries", out var libraries) &&
                        json.TryGetProperty("packageFolders", out var folders))
                    {
                        var target = targets.EnumerateObject().FirstOrDefault(t => !t.Name.Contains('/'));
                        if (target.Value.ValueKind == JsonValueKind.Undefined) target = targets.EnumerateObject().First();
                        foreach (var library in target.Value.EnumerateObject())
                        {
                            if (!libraries.TryGetProperty(library.Name, out var info) || !info.TryGetProperty("path", out var relative) ||
                                !info.TryGetProperty("type", out var type) || type.GetString() != "package") continue;
                            var group = library.Value.TryGetProperty("compile", out var compile) ? compile :
                                library.Value.TryGetProperty("runtime", out var runtime) ? runtime : default;
                            if (group.ValueKind != JsonValueKind.Object) continue;
                            foreach (var asset in group.EnumerateObject().Where(a => a.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
                            {
                                var found = folders.EnumerateObject().Select(f => Path.Combine(f.Name,
                                    Local(relative.GetString()!), Local(asset.Name))).FirstOrDefault(File.Exists);
                                if (found is null) { complete = false; continue; }
                                AddReference(found);
                            }
                        }
                    }
                }
                foreach (var reference in xml.Descendants().Where(e => e.Name.LocalName == "Reference"))
                {
                    var hint = reference.Elements().FirstOrDefault(e => e.Name.LocalName == "HintPath")?.Value;
                    if (hint is null) continue;
                    var full = Path.GetFullPath(Path.Combine(directory, Local(hint)));
                    if (File.Exists(full)) AddReference(full);
                    else complete = false;
                }
                foreach (var reference in xml.Descendants().Where(e => e.Name.LocalName == "ProjectReference"))
                    if (reference.Attribute("Include")?.Value is { } include) Visit(Path.Combine(directory, Local(include)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or System.Xml.XmlException or InvalidOperationException)
            { complete = false; }
        }
        Visit(project);
        var references = new List<MetadataReference>();
        var namespaces = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> defaultNamespaces = new(StringComparer.Ordinal), defaultAssemblies = new(StringComparer.OrdinalIgnoreCase);
        foreach (var assembly in paths.Values)
        {
            try
            {
                using var stream = File.OpenRead(assembly);
                using var pe = new PEReader(stream);
                if (!pe.HasMetadata) continue;
                var reader = pe.GetMetadataReader();
                var name = reader.GetString(reader.GetAssemblyDefinition().Name);
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var handle in reader.TypeDefinitions)
                {
                    var type = reader.GetTypeDefinition(handle);
                    var visibility = type.Attributes & TypeAttributes.VisibilityMask;
                    if (visibility == TypeAttributes.Public && !type.Namespace.IsNil) names.Add(reader.GetString(type.Namespace));
                }
                foreach (var handle in reader.ExportedTypes)
                {
                    var type = reader.GetExportedType(handle);
                    if (!type.Namespace.IsNil) names.Add(reader.GetString(type.Namespace));
                }
                namespaces[name] = names;
                if (name == "Cmf.Foundation.Common") ReadDefaults(pe, reader, defaultNamespaces, defaultAssemblies);
                references.Add(MetadataReference.CreateFromFile(assembly));
            }
            catch (Exception ex) when (ex is IOException or BadImageFormatException or UnauthorizedAccessException) { complete = false; }
        }
        foreach (var source in sourceNamespaces)
            namespaces.TryAdd(source.Key, source.Value);
        var compilation = CSharpCompilation.Create("DeeReferenceAnalysis", trees.Values, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        return new(compilation, namespaces, defaultNamespaces, defaultAssemblies, metadataAvailable, complete, sourceAssemblies, sourceNamespaces.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    private static string Local(string path) => path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
    private static readonly HashSet<string> Noise = new(StringComparer.OrdinalIgnoreCase)
        { "bin", "obj", ".git", "node_modules", "packages", "TestResults" };
    private static IEnumerable<string> SourceFiles(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*.cs")) yield return Path.GetFullPath(file);
        foreach (var child in Directory.EnumerateDirectories(directory).Where(d => !Noise.Contains(Path.GetFileName(d))))
            foreach (var file in SourceFiles(child)) yield return file;
    }

    private static readonly Dictionary<short, OpCode> Opcodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null)!).ToDictionary(o => o.Value);

    // These arrays are initialized as literals by the platform compiler. Decode only IL operands;
    // never invoke the type initializer or any product method. Unknown layouts stay unresolved.
    private static void ReadDefaults(PEReader pe, MetadataReader reader, HashSet<string> usings, HashSet<string> assemblies)
    {
        foreach (var handle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(handle);
            if (reader.GetString(type.Name) != "Compiler" ||
                reader.GetString(type.Namespace) != "Cmf.Foundation.Common.DynamicExecutionEngine") continue;
            foreach (var methodHandle in type.GetMethods())
            {
                var method = reader.GetMethodDefinition(methodHandle);
                if (reader.GetString(method.Name) != ".cctor" || method.RelativeVirtualAddress == 0) continue;
                var bytes = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes()!;
                var pending = new List<string>();
                var foundFields = new HashSet<string>();
                var decodedNamespaces = new HashSet<string>(StringComparer.Ordinal);
                var decodedAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var unsupportedInitializer = false;
                var initializerHasCall = false;
                for (var i = 0; i < bytes.Length;)
                {
                    short code = bytes[i++];
                    if (code == 0xfe) code = (short)(0xfe00 | bytes[i++]);
                    if (!Opcodes.TryGetValue(code, out var opcode)) return;
                    var length = opcode.OperandType switch
                    {
                        OperandType.InlineNone => 0,
                        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                        OperandType.InlineVar => 2,
                        OperandType.InlineI8 or OperandType.InlineR => 8,
                        OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(bytes, i),
                        _ => 4
                    };
                    if (opcode.FlowControl == FlowControl.Call) initializerHasCall = true;
                    if (opcode == OpCodes.Ldstr) pending.Add(reader.GetUserString(MetadataTokens.UserStringHandle(BitConverter.ToInt32(bytes, i) & 0xffffff)));
                    if (opcode == OpCodes.Stsfld)
                    {
                        var fieldHandle = MetadataTokens.Handle(BitConverter.ToInt32(bytes, i));
                        if (fieldHandle.Kind == HandleKind.FieldDefinition)
                        {
                            var field = reader.GetString(reader.GetFieldDefinition((FieldDefinitionHandle)fieldHandle).Name);
                            if (field is "CoreNamespaces" or "MesNamespaces")
                            {
                                foundFields.Add(field);
                                unsupportedInitializer |= initializerHasCall;
                                decodedNamespaces.UnionWith(pending);
                            }
                            if (field is "RuntimeReferences" or "CoreReferences" or "MesReferences")
                            {
                                foundFields.Add(field);
                                unsupportedInitializer |= initializerHasCall;
                                decodedAssemblies.UnionWith(pending.Where(s => s.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).Select(s => Path.GetFileNameWithoutExtension(s)));
                            }
                        }
                        pending.Clear();
                        initializerHasCall = false;
                    }
                    i += length;
                }
                if (!unsupportedInitializer && foundFields.SetEquals(new[] { "CoreNamespaces", "MesNamespaces", "RuntimeReferences", "CoreReferences", "MesReferences" }))
                {
                    usings.UnionWith(decodedNamespaces);
                    assemblies.UnionWith(decodedAssemblies);
                }
            }
        }
    }
}

public sealed record DeeReferenceProject(CSharpCompilation Compilation,
    Dictionary<string, HashSet<string>> AssemblyNamespaces, HashSet<string> DefaultNamespaces,
    HashSet<string> DefaultAssemblies, bool MetadataAvailable, bool Complete,
    Dictionary<SyntaxTree, string> SourceAssemblies, HashSet<string> SourceAssemblyNames)
{
    public bool DefaultsAvailable => DefaultNamespaces.Count > 0 && DefaultAssemblies.Count > 0;
}