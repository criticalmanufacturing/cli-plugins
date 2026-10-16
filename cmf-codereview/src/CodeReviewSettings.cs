using System.Text.Json;
using Cmf.CLI.Utilities;
using Cmf.Cli.Plugin.CodeReview.Enums;
using Cmf.Cli.Plugin.CodeReview.Rules;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cmf.Cli.Plugin.CodeReview;

/// <summary>Per-rule configuration entry in <c>.codereview.json</c>: disable a built-in rule or override its base severity.</summary>
public class RuleSetting
{
    public bool Enabled { get; set; } = true;

    /// <summary>Optional severity override ("Medium" | "High" | "Critical"). Applied to every finding the rule emits, including escalated ones.</summary>
    public string? Severity { get; set; }
}

/// <summary>
/// A project-defined custom rule declared as data in <c>.codereview.json</c>. Two kinds are supported,
/// each mapping onto an existing engine rule class:
/// <c>methodCallInLoop</c> (a <see cref="LoopSensitiveCallRule"/>: flags the named method calls inside loops,
/// with the same nested-loop/caller-induced escalation as the built-in loop rules) and
/// <c>entityPropertyAccess</c> (an <see cref="EntityPropertyAccessRule"/>: flags the named properties
/// dereferenced off a known entity-type reference).
/// </summary>
public class CustomRuleDefinition
{
    public string? Name { get; set; }
    public string? Kind { get; set; }

    // methodCallInLoop
    public List<string> MethodNames { get; set; } = new();
    /// <summary>Optional: only match when the receiver expression contains this text (case-insensitive), e.g. "query" or "fooService".</summary>
    public string? ReceiverContains { get; set; }
    public string? ActionNoun { get; set; }
    public string? InsideLoopFix { get; set; }
    public string? InducedByCallerFix { get; set; }

    // entityPropertyAccess
    public List<string> PropertyNames { get; set; } = new();
    public string? Severity { get; set; }
    public string? Description { get; set; }
    public string? SuggestedFix { get; set; }
    /// <summary>When true, only flag the access inside a loop body or LINQ lambda (same condition as ExcessiveLazyLoading).</summary>
    public bool OnlyInLoopOrLambda { get; set; }
}

/// <summary>
/// Optional per-project configuration, read from <c>.codereview.json</c> at the scanned repository's
/// root: per-rule enable/severity settings and declarative custom rules. Absence of the file means
/// all defaults — the scanner behaves exactly as if this class didn't exist.
/// </summary>
public class CodeReviewSettings
{
    public const string FileName = ".codereview.json";

    public Dictionary<string, RuleSetting> Rules { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<CustomRuleDefinition> CustomRules { get; set; } = new();

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>Loads settings from the repository root, or null when no settings file exists. Malformed JSON is a user-facing error, not a crash.</summary>
    public static CodeReviewSettings? Load(string repositoryRoot)
    {
        var path = Path.Combine(repositoryRoot, FileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var settings = JsonSerializer.Deserialize<CodeReviewSettings>(File.ReadAllText(path), SerializerOptions);
            settings?.Validate(path);
            return settings;
        }
        catch (JsonException e)
        {
            throw new CliException($"Could not parse {path}: {e.Message}");
        }
    }

    public bool IsRuleEnabled(string ruleName) =>
        !Rules.TryGetValue(ruleName, out var setting) || setting.Enabled;

    public Severity? GetSeverityOverride(string ruleName) =>
        Rules.TryGetValue(ruleName, out var setting) && setting.Severity is not null
            ? ParseSeverity(setting.Severity, $"rules.{ruleName}.severity")
            : null;

    /// <summary>Builds engine rule instances from the declarative custom-rule definitions.</summary>
    public IReadOnlyList<IReviewRule> BuildCustomRules()
    {
        var rules = new List<IReviewRule>();

        foreach (var definition in CustomRules)
        {
            if (string.IsNullOrWhiteSpace(definition.Name))
            {
                throw new CliException($"{FileName}: every custom rule needs a non-empty \"name\".");
            }

            rules.Add(definition.Kind switch
            {
                "methodCallInLoop" => BuildMethodCallInLoopRule(definition),
                "entityPropertyAccess" => BuildEntityPropertyAccessRule(definition),
                _ => throw new CliException(
                    $"{FileName}: custom rule '{definition.Name}' has unknown kind '{definition.Kind}'. Supported kinds: methodCallInLoop, entityPropertyAccess.")
            });
        }

        return rules;
    }

    private static IReviewRule BuildMethodCallInLoopRule(CustomRuleDefinition definition)
    {
        if (definition.MethodNames.Count == 0)
        {
            throw new CliException($"{FileName}: custom rule '{definition.Name}' (methodCallInLoop) needs a non-empty \"methodNames\" list.");
        }

        var methodNames = new HashSet<string>(definition.MethodNames, StringComparer.Ordinal);
        var receiverContains = definition.ReceiverContains;

        return new LoopSensitiveCallRule(
            definition.Name!,
            (invocation, _) => CodeReviewSyntaxTree.GetMemberName(invocation.Expression) is { } name &&
                methodNames.Contains(name.Identifier.Text) &&
                (receiverContains is null ||
                    (CodeReviewSyntaxTree.GetMemberTarget(invocation.Expression)?.ToString().Contains(receiverContains, StringComparison.OrdinalIgnoreCase) ?? false)),
            definition.ActionNoun ?? $"{definition.Name} call",
            definition.InsideLoopFix ?? "Hoist this call out of the loop and batch the work.",
            definition.InducedByCallerFix ?? "Hoist this call to the caller and pass preloaded data in, or make the method accept a collection so it can batch internally.");
    }

    private static IReviewRule BuildEntityPropertyAccessRule(CustomRuleDefinition definition)
    {
        if (definition.PropertyNames.Count == 0)
        {
            throw new CliException($"{FileName}: custom rule '{definition.Name}' (entityPropertyAccess) needs a non-empty \"propertyNames\" list.");
        }

        var propertyNames = new HashSet<string>(definition.PropertyNames, StringComparer.Ordinal);
        var severity = definition.Severity is null
            ? Severity.Medium
            : ParseSeverity(definition.Severity, $"custom rule '{definition.Name}' severity");

        return new EntityPropertyAccessRule(
            definition.Name!,
            propertyNames.Contains,
            severity,
            definition.Description ?? $"{definition.Name}: flagged entity property access.",
            definition.SuggestedFix ?? "Review this entity property access.",
            extraCondition: definition.OnlyInLoopOrLambda
                ? (context, access, line) =>
                    ((context.LoopState.Lines.Contains(line) && !context.LoopState.SingleExecutionLines.Contains(line)) ||
                     access.Ancestors().Any(a => a is LambdaExpressionSyntax or ArrowExpressionClauseSyntax)) &&
                    CmReceiverVariance.ReceiverVariesPerIteration(access)
                : null);
    }

    private static Severity ParseSeverity(string value, string context) =>
        Enum.TryParse<Severity>(value, ignoreCase: true, out var severity)
            ? severity
            : throw new CliException($"{FileName}: {context} has invalid severity '{value}'. Valid values: Medium, High, Critical.");

    private void Validate(string path)
    {
        // Force severity parsing up front so a bad value fails at load time with the file path, not mid-scan.
        foreach (var (ruleName, setting) in Rules)
        {
            if (setting.Severity is not null)
            {
                ParseSeverity(setting.Severity, $"rules.{ruleName}.severity");
            }
        }
    }
}
