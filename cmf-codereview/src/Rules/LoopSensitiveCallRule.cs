using Cmf.Cli.Plugin.CodeReview.Models;
using Cmf.Cli.Plugin.CodeReview.Enums;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cmf.Cli.Plugin.CodeReview.Rules;

/// <summary>
/// A call/invocation pattern that's worse when it sits inside a loop (severity escalates further
/// for nested loops), and is still flagged -- under the shared "LoopInducedByCaller" name -- when
/// it's not textually inside a loop but its enclosing method is itself transitively called from a
/// loop elsewhere. LoadInsideLoop/ResolveInsideLoop/QueryExecuteInsideLoop are all one instantiation
/// of this class each; a future rule of the same shape is another instantiation, not new code.
/// </summary>
public class LoopSensitiveCallRule : IReviewRule
{
    public const string InducedByCallerRuleName = "LoopInducedByCaller";

    private readonly Func<InvocationExpressionSyntax, RuleFileContext, bool> _isMatch;
    private readonly string _insideLoopRuleName;
    private readonly string _actionNoun;
    private readonly string _insideLoopFix;
    private readonly string _inducedByCallerFix;
    private readonly Func<InvocationExpressionSyntax, RuleFileContext, string?>? _fixOverride;

    public LoopSensitiveCallRule(
        string insideLoopRuleName,
        Func<InvocationExpressionSyntax, RuleFileContext, bool> isMatch,
        string actionNoun,
        string insideLoopFix,
        string inducedByCallerFix,
        Func<InvocationExpressionSyntax, RuleFileContext, string?>? fixOverride = null)
    {
        _insideLoopRuleName = insideLoopRuleName;
        _isMatch = isMatch;
        _actionNoun = actionNoun;
        _insideLoopFix = insideLoopFix;
        _inducedByCallerFix = inducedByCallerFix;
        _fixOverride = fixOverride;
    }

    public IEnumerable<string> EscalatableRuleNames => new[] { _insideLoopRuleName };

    public IEnumerable<RuleMatch> FindMatches(RuleFileContext context)
    {
        foreach (var invocation in context.Root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (!_isMatch(invocation, context))
            {
                continue;
            }

            var line = CodeReviewSyntaxTree.StartLine(invocation);
            var methodName = context.GetMethodNameForLine(line);

            if (context.LoopState.Lines.Contains(line) && !context.LoopState.SingleExecutionLines.Contains(line))
            {
                var nestedLoopDepth = context.LoopState.DepthByLine.TryGetValue(line, out var d) ? d : 1;
                var isNestedLoop = nestedLoopDepth >= 2;
                var transitiveLoopNote = context.GetTransitiveLoopNote(methodName);
                var isCompoundedByCaller = transitiveLoopNote.Length > 0;
                var severity = (isNestedLoop || isCompoundedByCaller) ? Severity.Critical : Severity.High;

                var notes = new List<string>();
                var reasons = new List<string>();
                if (isNestedLoop)
                {
                    notes.Add($"This is inside a nested loop ({nestedLoopDepth} levels deep) here, so the cost multiplies with the size of every enclosing loop, not just one.");
                    reasons.Add(Escalation.NestedLoopReason);
                }
                if (isCompoundedByCaller)
                {
                    notes.Add(transitiveLoopNote.Trim());
                    reasons.Add(Escalation.CalledFromLoopReason);
                }
                var compoundingNote = notes.Count > 0 ? " " + string.Join(" ", notes) : string.Empty;
                var escalation = reasons.Count > 0
                    ? new Escalation
                    {
                        BaseSeverity = Severity.High,
                        Reasons = reasons,
                        NestedLoopDepth = isNestedLoop ? nestedLoopDepth : null
                    }
                    : null;

                yield return new RuleMatch(_insideLoopRuleName, line, severity,
                    $"{_actionNoun} appears inside a loop body.{compoundingNote}", _fixOverride?.Invoke(invocation, context) ?? _insideLoopFix, escalation);
            }
            else if (methodName is not null)
            {
                var exposure = context.GetLoopExposure(methodName);
                if (exposure is not null && exposure.Kind == LoopExposure.CalledFromLoopKind)
                {
                    var chain = CmLoopExposureFinder.FormatChain(methodName, exposure);
                    var exposureNote = $"This call is not inside a loop here. Call chain to the loop that causes it to repeat: {chain}.";
                    yield return new RuleMatch(InducedByCallerRuleName, line, Severity.Critical,
                        $"{_actionNoun} runs once per call to this method, and this method is called from a loop elsewhere. {exposureNote}",
                        _fixOverride?.Invoke(invocation, context) ?? _inducedByCallerFix);
                }
            }
        }
    }
}
