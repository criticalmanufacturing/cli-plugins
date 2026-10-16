using Cmf.Cli.Plugin.CodeReview.Models;
using Cmf.Cli.Plugin.CodeReview.Enums;

namespace Cmf.Cli.Plugin.CodeReview.Rules;

/// <summary>A single occurrence of a rule firing: where, how severely, and what to say about it. <paramref name="Escalation"/> is set only when the severity was raised above the rule's base severity.</summary>
public record RuleMatch(string RuleName, int Line, Severity Severity, string Description, string SuggestedFix, Escalation? Escalation = null);

/// <summary>
/// A single review rule: given a file's precomputed <see cref="RuleFileContext"/>, decide which
/// lines match and how severely.
///
/// To add a new rule:
/// - If it's "a call/expression pattern that's worse inside a loop, or in a method transitively
///   called from a loop elsewhere" (the same shape as LoadInsideLoop/ResolveInsideLoop/
///   QueryExecuteInsideLoop), don't write a new class -- add another instantiation of
///   <see cref="LoopSensitiveCallRule"/> to <c>CmFrameworkScanner.Rules</c>, giving it a node
///   predicate and rule name.
/// - If it's "a property dereferenced off a known entity-type reference" (the same shape as
///   LoadReferenceForFkOnly/ExcessiveLazyLoading -- <c>x.Material.Description</c>, including the
///   null-conditional <c>x.Material?.Description</c> form), add another instantiation of
///   <see cref="EntityPropertyAccessRule"/> instead, giving it a property-name predicate and an
///   optional extra condition.
/// - Otherwise, write a small class implementing this interface (see
///   <c>DirectEntityInstantiationRule</c> in <c>EntityRules.cs</c> for the pattern: walk whatever
///   syntax node kind is relevant via <c>context.Root.DescendantNodes().OfType&lt;...&gt;()</c>, yield
///   a <see cref="RuleMatch"/> per hit), and add an instance of it to <c>CmFrameworkScanner.Rules</c>.
///
/// A rule only decides *if/where/how severely* something matches -- it does not need to (and
/// should not) attach cross-reference, service-endpoint, or method-name enrichment to its
/// findings; the scanner orchestrator applies that uniformly to every match after rules run.
/// </summary>
public interface IReviewRule
{
    /// <summary>
    /// Rule name(s) this rule can emit whose severity is escalatable (nested-loop depth or
    /// caller-induced loop exposure). Empty for the vast majority of rules, which always fire at a
    /// single fixed severity.
    /// </summary>
    IEnumerable<string> EscalatableRuleNames { get; }

    IEnumerable<RuleMatch> FindMatches(RuleFileContext context);
}
