using System.Text.Json;
using System.Text.RegularExpressions;
using Cmf.Cli.Plugin.CodeReview.Models;
using Cmf.Cli.Plugin.CodeReview.Rules;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cmf.Cli.Plugin.CodeReview;

/// <summary>Read-only deployment metadata shared by all rule contexts in one scan. No Markdown inputs.</summary>
public sealed class ProcessRuleRepositoryContext
{
    private readonly Dictionary<string, ProcessRuleInfo> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Package, string Version, string Table), string> _schemaChanges = new();
    private readonly Dictionary<string, List<string>> _orderProblems = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Regex VersionSegment = new(@"^\d+\.\d+\.\d+(?:\.\d+)?$", RegexOptions.Compiled);

    public ProcessRuleInfo? GetInfo(string file) => _files.GetValueOrDefault(Path.GetFullPath(file));
    public IReadOnlyList<string> GetOrderProblems(string file) => _orderProblems.GetValueOrDefault(Path.GetFullPath(file)) ?? [];

    public string? GetSchemaChange(ProcessRuleInfo info, string table) =>
        info.PackageDirectory is not null && info.Version is not null
            ? _schemaChanges.GetValueOrDefault((info.PackageDirectory, info.Version, table)) : null;

    /// <summary>Fallback for explicit roots and isolated syntax-rule tests.</summary>
    public static ProcessRuleInfo? ClassifyPath(string file)
    {
        var segments = file.Replace('\\', '/').Split('/');
        var start = Array.FindIndex(segments, s => s.Equals("ProcessRules", StringComparison.OrdinalIgnoreCase));
        if (start < 1 || !segments[start - 1].Equals("DEEs", StringComparison.OrdinalIgnoreCase)) return null;
        var tail = segments.Skip(start + 1).ToArray();
        if (tail.Length == 2 && tail[0].Equals("Services", StringComparison.OrdinalIgnoreCase))
            return new(null, "Services");
        if (tail.Length == 3 && VersionSegment.IsMatch(tail[0]) && tail[1] is "Before" or "After")
            return new(tail[0], tail[1]);
        if (tail.Length == 3 && tail[0] is "EntityTypes" or "EntityTypesAttributes" && VersionSegment.IsMatch(tail[1]))
            return new(tail[1], tail[0]);
        return new(null, "Unknown");
    }

    public static ProcessRuleRepositoryContext Build(string repositoryRoot, IEnumerable<string> files, CodeReviewFileCache? fileCache = null)
    {
        var context = new ProcessRuleRepositoryContext();
        var content = CodeReviewPaths.DiscoverDataPackageContent(repositoryRoot).ToList();
        var processEntries = content.Where(c => CodeReviewPaths.IsProcessRuleContent(c.ContentType))
            .Select(c => (Content: c, Pattern: SourcePattern(c.Source))).ToList();
        foreach (var file in files)
        {
            var fullFile = Path.GetFullPath(file);
            foreach (var (entry, pattern) in processEntries)
            {
                var relative = Path.GetRelativePath(entry.Directory, fullFile).Replace('\\', '/');
                var match = pattern.Match(relative);
                if (!match.Success) continue;
                var fallback = ClassifyPath(fullFile);
                var version = match.Groups["version"].Success ? match.Groups["version"].Value : fallback?.Version;
                var phase = entry.ContentType.Equals("EntityTypes", StringComparison.OrdinalIgnoreCase) ? "EntityTypes"
                    : entry.ContentType.Equals("ProcessRulesPre", StringComparison.OrdinalIgnoreCase) ? "Before"
                    : relative.Split('/').Contains("EntityTypesAttributes") ? "EntityTypesAttributes"
                    : relative.Split('/').Contains("Services") ? "Services" : "After";
                var masterDataOrders = content.Where(c => c.Directory == entry.Directory && c.ContentType.Equals("MasterData", StringComparison.OrdinalIgnoreCase))
                    .Select(c => c.Order).ToList();
                string? problem = fallback is { Phase: not "Unknown" } && fallback.Phase != phase
                    ? $"Folder phase {fallback.Phase} is packaged as {entry.ContentType} ({phase})." : null;
                if (masterDataOrders.Count > 0 && (phase is "Before" or "EntityTypes" && entry.Order > masterDataOrders.Min() ||
                    phase is "After" or "EntityTypesAttributes" or "Services" && entry.Order < masterDataOrders.Max()))
                    problem = $"{phase} content is on the wrong side of MasterData in cmfpackage.json contentToPack ordering.";
                context._files[fullFile] = new(version, fallback is { Phase: not "Unknown" } ? fallback.Phase : phase,
                    entry.Directory, entry.ContentType, entry.Order, problem);
                break;
            }
            if (!context._files.ContainsKey(fullFile) && ClassifyPath(fullFile) is { } info)
            {
                // An unlisted standard path still gets syntax validation, with no fabricated package facts.
                var package = content.Where(c => fullFile.StartsWith(c.Directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(c => c.Directory.Length).FirstOrDefault()?.Directory;
                context._files[fullFile] = info with { PackageDirectory = package };
            }
        }
        if (context._files.Count != 0)
        {
            context.IndexSchemaChanges(repositoryRoot, content);
            context.IndexDependencies(repositoryRoot, fileCache);
        }
        return context;
    }

    private void IndexDependencies(string repositoryRoot, CodeReviewFileCache? fileCache)
    {
        var creators = new List<(string File, ProcessRuleInfo Info, string Entity)>();
        var consumers = new List<(string File, ProcessRuleInfo Info, string Entity)>();
        foreach (var (file, info) in _files.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (info.Version is null || info.PackageDirectory is null) continue;
            var root = fileCache?.Get(file).Root ?? CodeReviewSyntaxTree.Parse(File.ReadAllText(file)).Root;
            foreach (var @class in root.DescendantNodes().OfType<ClassDeclarationSyntax>()
                .Where(c => c.BaseList?.Types.Any(t => CodeReviewSyntaxTree.LastIdentifierSegment(t.Type.ToString()) == "DeeDevBase") == true))
            {
                var action = @class.Members.OfType<MethodDeclarationSyntax>().FirstOrDefault(m => m.Identifier.ValueText == "DeeActionCode");
                if (action is null) continue;
                var syntax = new ProcessRuleSyntax(@class, action);
                foreach (var call in syntax.Calls)
                {
                    var receiver = ProcessRuleSyntax.Receiver(call);
                    var isEntityType = syntax.TypeOf(receiver) is "IEntityType" or "EntityType";
                    var name = ProcessRuleSyntax.Name(call);
                    if (name == "Create" && isEntityType && syntax.AssignedValue(ProcessRuleSyntax.Tokens(receiver), "Name", call) is { } assigned &&
                        syntax.StringValue(assigned) is { } created) creators.Add((file, info, created));
                    if ((name == "LoadEntityType" || name == "Load" && isEntityType) && call.ArgumentList.Arguments.FirstOrDefault()?.Expression is { } value &&
                        syntax.StringValue(value) is { } loaded) consumers.Add((file, info, loaded));
                }
            }
        }
        foreach (var consumer in consumers.Distinct())
        {
            var matching = creators.Where(c => c.File != consumer.File && c.Info.PackageDirectory == consumer.Info.PackageDirectory &&
                c.Info.Version == consumer.Info.Version && c.Entity == consumer.Entity).ToList();
            // Multiple creators are ambiguous. Do not invent a dependency edge to one of them.
            if (matching.Count != 1) continue;
            var creator = matching[0];
            var creatorOrder = creator.Info.ContentOrder >= 0 ? creator.Info.ContentOrder : PhaseOrder(creator.Info.Phase);
            var consumerOrder = consumer.Info.ContentOrder >= 0 ? consumer.Info.ContentOrder : PhaseOrder(consumer.Info.Phase);
            if (creatorOrder < consumerOrder || creatorOrder == consumerOrder &&
                string.Compare(Path.GetFileName(creator.File), Path.GetFileName(consumer.File), StringComparison.Ordinal) < 0) continue;
            if (!_orderProblems.TryGetValue(consumer.File, out var problems)) _orderProblems[consumer.File] = problems = [];
            problems.Add($"Entity type '{consumer.Entity}' is loaded before its same-version creation rule '{CodeReviewPaths.GetRelativePath(repositoryRoot, creator.File)}' in declared content/alphabetical file order.");
        }
    }

    private static int PhaseOrder(string phase) => phase switch
    {
        "EntityTypes" => 0, "Before" => 1, "After" => 3, "EntityTypesAttributes" => 4, "Services" => 5, _ => 6
    };

    private static Regex SourcePattern(string source)
    {
        var normalized = source.Replace('\\', '/');
        if (normalized.StartsWith("./", StringComparison.Ordinal)) normalized = normalized[2..];
        var pattern = Regex.Escape(normalized)
            .Replace(Regex.Escape("$(version)"), @"(?<version>\d+\.\d+\.\d+(?:\.\d+)?)")
            .Replace(@"\*", ".*").Replace(@"\?", ".");
        return new Regex("^" + pattern + "$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    }

    private void IndexSchemaChanges(string repositoryRoot, List<DataPackageContent> content)
    {
        foreach (var package in content.GroupBy(c => c.Directory).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var definitions = new Dictionary<string, string>(StringComparer.Ordinal);
            var properties = new Dictionary<(string, string), Dictionary<string, string>>();
            var jsonFiles = new List<(string Path, string Version)>();
            foreach (var entry in package.Where(c => c.ContentType.Equals("MasterData", StringComparison.OrdinalIgnoreCase)))
            {
                var literal = entry.Source.Replace('\\', '/').Split('/').TakeWhile(s => !s.Contains('$') && !s.Contains('*') && !s.Contains('?'));
                var root = Path.Combine(entry.Directory, Path.Combine(literal.ToArray()));
                if (!Directory.Exists(root)) continue;
                var pattern = SourcePattern(entry.Source);
                foreach (var file in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories))
                {
                    var match = pattern.Match(Path.GetRelativePath(entry.Directory, file).Replace('\\', '/'));
                    if (match.Success && match.Groups["version"].Success) jsonFiles.Add((file, match.Groups["version"].Value));
                }
            }
            foreach (var (file, version) in jsonFiles.Distinct().OrderBy(f => System.Version.Parse(f.Version)).ThenBy(f => f.Path, StringComparer.Ordinal))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
                    foreach (var section in doc.RootElement.EnumerateObject())
                    {
                        var kind = section.Name[(section.Name.LastIndexOf('>') + 1)..];
                        var tableDefinition = kind is "SmartTable" or "GenericTable";
                        var tableProperty = kind is "SmartTableProperties" or "GenericTableProperties";
                        if ((!tableDefinition && !tableProperty) || section.Value.ValueKind != JsonValueKind.Object) continue;
                        foreach (var row in section.Value.EnumerateObject().Select(p => p.Value).Where(v => v.ValueKind == JsonValueKind.Object))
                        {
                            var table = Text(row, tableDefinition ? "Name" : kind == "SmartTableProperties" ? "SmartTable" : "GenericTable");
                            if (table is null) continue;
                            bool changed;
                            if (tableDefinition)
                            {
                                changed = !definitions.ContainsKey(table);
                                definitions.TryAdd(table, version);
                            }
                            else
                            {
                                var name = Text(row, "Name");
                                if (name is null) continue;
                                // Ignore description/precedence/row-data edits: they don't define table structure.
                                changed = !properties.TryGetValue((table, name), out var fields);
                                fields ??= new Dictionary<string, string>(StringComparer.Ordinal);
                                foreach (var key in new[] { "ScalarType", "ScalarSize", "ScalarPrecision", "ReferenceType", "ReferenceName", "IsArray", "IsMandatory", "IsEnabled", "IsIndexed" })
                                {
                                    if (!row.TryGetProperty(key, out var value)) continue;
                                    if (!fields.TryGetValue(key, out var previous) || previous != value.ToString()) changed = true;
                                    fields[key] = value.ToString();
                                }
                                properties[(table, name)] = fields;
                            }
                            if (changed) _schemaChanges.TryAdd((package.Key, version, table), CodeReviewPaths.GetRelativePath(repositoryRoot, file));
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    Console.Error.WriteLine($"warning: Process Rule schema context unavailable for '{CodeReviewPaths.GetRelativePath(repositoryRoot, file)}': {ex.Message}");
                }
            }
        }
    }

    private static string? Text(JsonElement row, string key) =>
        row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
