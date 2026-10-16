using Cmf.Cli.Plugin.CodeReview.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cmf.Cli.Plugin.CodeReview;

/// <summary>
/// Detects which lines sit inside a for/foreach/while/do loop body, and how many loops deep
/// (nesting) each such line is, via the C# syntax tree -- so a `foreach` statement and a
/// `.ForEach()` LINQ call are never confused, since they're different node kinds entirely.
/// </summary>
public static class CmLoopAnalyzer
{
    /// <summary>
    /// Walks the syntax tree and reports which lines sit inside a for/foreach/while/do loop body and at
    /// what nesting depth, including brace-less single-statement loop bodies. Pass
    /// <paramref name="computeSingleExecutionLines"/> = false when the caller only needs loop membership
    /// (e.g. the call-graph builder) and can skip the single-execution tail analysis.
    /// </summary>
    public static LoopState GetLoopState(IReadOnlyList<string> lines, bool computeSingleExecutionLines = true)
    {
        if (lines.Count == 0)
        {
            return new LoopState();
        }

        var (_, root) = CodeReviewSyntaxTree.Parse(lines);
        return GetLoopState(root, computeSingleExecutionLines);
    }

    /// <summary>Same analysis over an already-parsed root, so cached parses aren't re-done.</summary>
    public static LoopState GetLoopState(CompilationUnitSyntax root, bool computeSingleExecutionLines = true)
    {
        var state = new LoopState();

        // One tree walk collects everything the passes below need, instead of each re-walking the
        // whole tree (loop marking, single-execution, and local-function exposure previously did 4+
        // full DescendantNodes passes between them). Blocks/local functions/invocations are only
        // collected when something downstream will use them.
        var loopBodies = new List<StatementSyntax>();
        List<BlockSyntax>? blocks = computeSingleExecutionLines ? new List<BlockSyntax>() : null;
        var localFunctions = new List<LocalFunctionStatementSyntax>();
        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case ForStatementSyntax f: loopBodies.Add(f.Statement); break;
                case ForEachStatementSyntax fe: loopBodies.Add(fe.Statement); break;
                case WhileStatementSyntax w: loopBodies.Add(w.Statement); break;
                case DoStatementSyntax d: loopBodies.Add(d.Statement); break;
                case BlockSyntax b: blocks?.Add(b); break;
                case LocalFunctionStatementSyntax lf: localFunctions.Add(lf); break;
            }
        }

        foreach (var body in loopBodies)
        {
            var depth = CountAncestorLoops(body.Parent!) + 1;
            var startLine = CodeReviewSyntaxTree.StartLine(body);
            var endLine = CodeReviewSyntaxTree.EndLine(body);

            for (var lineNumber = startLine; lineNumber <= endLine; lineNumber++)
            {
                state.Lines.Add(lineNumber);
                if (!state.DepthByLine.TryGetValue(lineNumber, out var existingDepth) || depth > existingDepth)
                {
                    state.DepthByLine[lineNumber] = depth;
                }
            }
        }

        if (state.Lines.Count == 0)
        {
            return state;
        }

        if (computeSingleExecutionLines)
        {
            MarkSingleExecutionLines(blocks!, state);
        }

        if (localFunctions.Count > 0)
        {
            MarkLocalFunctionLoopExposure(root, localFunctions, state);
        }

        return state;
    }

    /// <summary>Beyond this, extra loop nesting doesn't change any rule decision (escalation only asks "&gt;= 2?"); capping also makes the exposure fixpoint terminate on recursive/mutually-recursive local functions.</summary>
    private const int MaxLoopExposureDepth = 8;

    /// <summary>
    /// A local function called (by bare name) from inside a loop runs its whole body once per
    /// iteration, even though the body sits lexically outside the loop. This marks such a local
    /// function's body lines as loop-exposed so the loop-sensitive rules fire on the calls inside
    /// it -- e.g. a <c>material.Load()</c> in a helper that a <c>foreach</c> calls each iteration.
    /// Purely syntactic and single-file: local functions have unambiguous lexical scope, so a bare
    /// call resolves exactly to its declaration with no call graph or semantic model. Exposure
    /// propagates transitively (helper calls helper) and compounds with real loops -- a body line
    /// already inside an inner loop gets its depth raised by the call-site's loop depth, so
    /// call-in-a-loop + load-in-an-inner-loop escalates to the nested-loop severity.
    /// </summary>
    private static void MarkLocalFunctionLoopExposure(CompilationUnitSyntax root, List<LocalFunctionStatementSyntax> localFunctions, LoopState state)
    {
        // Physical depths are captured before we mutate DepthByLine, so compounding uses the real
        // loop nesting at each site, not a value we already raised.
        var physicalDepth = new Dictionary<int, int>(state.DepthByLine);

        // Call sites grouped by the local function they target: each records the local function
        // lexically containing the call (null when a regular method contains it) and the loop depth
        // at the call. Self-recursive calls (container == target) are skipped entirely: recursion is
        // a different pattern from "helper invoked inside a loop", its cost isn't a loop count we can
        // reason about, and "batch into a collection" isn't a meaningful fix for it -- counting it
        // would flag every recursive tree-walker.
        var edgesByTarget = new Dictionary<LocalFunctionStatementSyntax, List<(LocalFunctionStatementSyntax? Container, int CallDepth)>>();
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not IdentifierNameSyntax identifier)
            {
                continue;
            }

            var target = ResolveLocalFunction(invocation, identifier.Identifier.Text);
            if (target is null)
            {
                continue;
            }

            var container = invocation.Ancestors().OfType<LocalFunctionStatementSyntax>().FirstOrDefault();
            if (ReferenceEquals(container, target))
            {
                continue;
            }

            if (!edgesByTarget.TryGetValue(target, out var callers))
            {
                callers = new List<(LocalFunctionStatementSyntax?, int)>();
                edgesByTarget[target] = callers;
            }

            var callLine = CodeReviewSyntaxTree.StartLine(invocation);
            callers.Add((container, physicalDepth.GetValueOrDefault(callLine, 0)));
        }

        // exposure[F] = total enclosing loop depth F's body runs under = max over F's call sites of
        // (loop depth at the call + exposure of the local function that made it). Memoized DFS with
        // a visiting set so a mutual-recursion cycle contributes 0 on its back-edge instead of
        // inflating without bound.
        var exposureCache = new Dictionary<LocalFunctionStatementSyntax, int>();
        var visiting = new HashSet<LocalFunctionStatementSyntax>();

        int Exposure(LocalFunctionStatementSyntax function)
        {
            if (exposureCache.TryGetValue(function, out var cached))
            {
                return cached;
            }

            if (!visiting.Add(function))
            {
                return 0;
            }

            var best = 0;
            if (edgesByTarget.TryGetValue(function, out var callers))
            {
                foreach (var (container, callDepth) in callers)
                {
                    var containerExposure = container is not null ? Exposure(container) : 0;
                    best = Math.Max(best, Math.Min(callDepth + containerExposure, MaxLoopExposureDepth));
                }
            }

            visiting.Remove(function);
            exposureCache[function] = best;
            return best;
        }

        foreach (var localFunction in localFunctions)
        {
            var bodyExposure = Exposure(localFunction);
            if (bodyExposure <= 0 || localFunction.Body is null)
            {
                continue;
            }

            // Lines of a nested local function declared inside this one are exposed only if that
            // nested one is itself called from an exposed site -- it gets its own edge, so skip its
            // span here rather than blanket-marking it.
            var nestedSpans = localFunction.Body.DescendantNodes().OfType<LocalFunctionStatementSyntax>()
                .Select(n => (Start: CodeReviewSyntaxTree.StartLine(n), End: CodeReviewSyntaxTree.EndLine(n)))
                .ToList();

            var start = CodeReviewSyntaxTree.StartLine(localFunction.Body);
            var end = CodeReviewSyntaxTree.EndLine(localFunction.Body);
            for (var line = start; line <= end; line++)
            {
                if (nestedSpans.Any(span => line >= span.Start && line <= span.End))
                {
                    continue;
                }

                var effectiveDepth = Math.Min(bodyExposure + physicalDepth.GetValueOrDefault(line, 0), MaxLoopExposureDepth);
                state.Lines.Add(line);
                if (!state.DepthByLine.TryGetValue(line, out var current) || effectiveDepth > current)
                {
                    state.DepthByLine[line] = effectiveDepth;
                }
            }
        }
    }

    /// <summary>Resolves a bare-name call to the local function it binds to: the nearest enclosing block that declares a local function of that name. Null when none is in scope (i.e. it's a regular method/other call).</summary>
    private static LocalFunctionStatementSyntax? ResolveLocalFunction(SyntaxNode callSite, string name)
    {
        foreach (var ancestor in callSite.Ancestors())
        {
            if (ancestor is BlockSyntax block)
            {
                var match = block.Statements.OfType<LocalFunctionStatementSyntax>().FirstOrDefault(f => f.Identifier.Text == name);
                if (match is not null)
                {
                    return match;
                }
            }

            if (ancestor is BaseMethodDeclarationSyntax or BaseTypeDeclarationSyntax)
            {
                break;
            }
        }

        return null;
    }

    /// <summary>
    /// How execution of a statement (and everything after it in its block) can complete. Composed
    /// syntactically -- no semantic model or compilation involved, since Roslyn's region
    /// control-flow analysis costs proportional to the whole enclosing method, which made it
    /// prohibitively slow on real customization files with multi-thousand-line methods.
    /// </summary>
    [Flags]
    private enum TailCompletion
    {
        None = 0,
        FallsThrough = 1,
        Returns = 2,       // return or throw: unwinds the whole method, safe at any loop depth
        Breaks = 4,        // break bound to the loop under analysis: safe only at depth 1
        Continues = 8,     // continue: explicitly loops back, never safe
        SwitchBreaks = 16, // break bound to an enclosing switch: becomes FallsThrough at the switch
        Unknown = 32       // goto, labeled, yield, try, ... -- conservatively unsafe
    }

    /// <summary>
    /// Finds, for every loop (at any nesting depth), the statements after which the loop is
    /// guaranteed to exit rather than fall through or continue -- meaning a call there runs at most
    /// once despite sitting inside a loop. Checked both for the loop body's top-level statement
    /// tails and for tails inside nested non-loop blocks (e.g. <c>if (found) { Load(); break; }</c>:
    /// whenever the load runs, the loop exits right after, so it runs at most once even though the
    /// enclosing loop keeps iterating when the condition is false). A guaranteed return/throw is
    /// safe at any depth, since it unwinds the whole method regardless of how many loops enclose
    /// it. A guaranteed break is only trusted when this loop itself has no enclosing loop -- a
    /// break only exits its own loop, so an outer loop could still re-enter it and re-run the call.
    /// </summary>
    private static void MarkSingleExecutionLines(IReadOnlyList<BlockSyntax> blocks, LoopState state)
    {
        // Each block is visited once and attributed to its innermost enclosing loop, rather than
        // re-walking every loop's whole body subtree (which made a block K loops deep get inspected
        // K times -- quadratic on the deeply-nested loops in real batch code). A block only counts
        // toward the loop whose body block IS its innermost enclosing loop body: blocks inside a
        // nested loop are that loop's own separate repetition question, and blocks whose innermost
        // loop has a brace-less single-statement body aren't considered at all (InnermostEnclosingLoopBody
        // returns null). Blocks inside a try body are skipped: a catch in the same loop can swallow
        // the exception and fall through, so "this tail always exits" is deceptive there.
        foreach (var candidate in blocks)
        {
            if (candidate.Statements.Count == 0)
            {
                continue;
            }

            // Cheap reject before the ancestor walk: a block can only be inside a loop if its start
            // line was marked as loop-body coverage. Blocks in non-loop code (the majority in most
            // files) skip the InnermostEnclosingLoopBody ancestor walk entirely.
            if (!state.Lines.Contains(CodeReviewSyntaxTree.StartLine(candidate)))
            {
                continue;
            }

            var loopBody = InnermostEnclosingLoopBody(candidate);
            if (loopBody is null || IsInsideTryBody(candidate, loopBody))
            {
                continue;
            }

            var loopDepth = CountAncestorLoops(loopBody.Parent!) + 1;
            MarkGuaranteedExitTail(candidate, loopDepth, state);
        }
    }

    private static void MarkGuaranteedExitTail(BlockSyntax block, int loopDepth, LoopState state)
    {
        var statements = block.Statements;

        // Compute tail completions backwards in one pass: tail(i) = stmt(i) composed with tail(i+1).
        var tail = TailCompletion.FallsThrough;
        var earliestSafe = -1;
        var tails = new TailCompletion[statements.Count];
        for (var i = statements.Count - 1; i >= 0; i--)
        {
            tail = Compose(GetCompletions(statements[i]), tail);
            tails[i] = tail;
        }

        for (var i = 0; i < statements.Count; i++)
        {
            if (IsSafeExit(tails[i], loopDepth))
            {
                earliestSafe = i;
                break;
            }
        }

        if (earliestSafe < 0)
        {
            return;
        }

        var startLine = CodeReviewSyntaxTree.StartLine(statements[earliestSafe]);
        var endLine = CodeReviewSyntaxTree.EndLine(statements[^1]);
        for (var lineNumber = startLine; lineNumber <= endLine; lineNumber++)
        {
            if (state.DepthByLine.TryGetValue(lineNumber, out var lineDepth) && lineDepth == loopDepth)
            {
                state.SingleExecutionLines.Add(lineNumber);
            }
        }
    }

    private static bool IsSafeExit(TailCompletion completion, int loopDepth) =>
        (completion & (TailCompletion.FallsThrough | TailCompletion.Continues | TailCompletion.SwitchBreaks | TailCompletion.Unknown)) == 0 &&
        ((completion & TailCompletion.Breaks) == 0 || loopDepth == 1);

    /// <summary>"Run <paramref name="first"/>, then <paramref name="rest"/> if it falls through."</summary>
    private static TailCompletion Compose(TailCompletion first, TailCompletion rest) =>
        (first & TailCompletion.FallsThrough) != 0 ? (first & ~TailCompletion.FallsThrough) | rest : first;

    private static TailCompletion GetCompletions(StatementSyntax statement) => statement switch
    {
        ReturnStatementSyntax or ThrowStatementSyntax => TailCompletion.Returns,
        BreakStatementSyntax => TailCompletion.Breaks,
        ContinueStatementSyntax => TailCompletion.Continues,
        BlockSyntax block => SequenceCompletions(block.Statements),
        IfStatementSyntax ifStatement =>
            GetCompletions(ifStatement.Statement) |
            (ifStatement.Else is null ? TailCompletion.FallsThrough : GetCompletions(ifStatement.Else.Statement)),
        SwitchStatementSyntax switchStatement => GetSwitchCompletions(switchStatement),
        // A nested loop swallows its own breaks/continues and (conservatively) falls through.
        ForStatementSyntax or ForEachStatementSyntax or ForEachVariableStatementSyntax
            or WhileStatementSyntax or DoStatementSyntax => TailCompletion.FallsThrough,
        UsingStatementSyntax usingStatement when usingStatement.Statement is not null => GetCompletions(usingStatement.Statement),
        LockStatementSyntax lockStatement => GetCompletions(lockStatement.Statement),
        CheckedStatementSyntax checkedStatement => SequenceCompletions(checkedStatement.Block.Statements),
        UnsafeStatementSyntax unsafeStatement => SequenceCompletions(unsafeStatement.Block.Statements),
        ExpressionStatementSyntax or LocalDeclarationStatementSyntax or EmptyStatementSyntax
            or LocalFunctionStatementSyntax => TailCompletion.FallsThrough,
        // try (a catch may swallow and fall through), goto, labeled, yield, ...
        _ => TailCompletion.Unknown
    };

    private static TailCompletion SequenceCompletions(IReadOnlyList<StatementSyntax> statements)
    {
        var result = TailCompletion.FallsThrough;
        foreach (var statement in statements)
        {
            if ((result & TailCompletion.FallsThrough) == 0)
            {
                break; // rest is unreachable
            }

            result = (result & ~TailCompletion.FallsThrough) | GetCompletions(statement);
        }

        return result;
    }

    private static TailCompletion GetSwitchCompletions(SwitchStatementSyntax switchStatement)
    {
        var result = TailCompletion.None;
        var hasDefault = false;

        foreach (var section in switchStatement.Sections)
        {
            if (section.Labels.Any(l => l is DefaultSwitchLabelSyntax))
            {
                hasDefault = true;
            }

            var sectionCompletion = MapLoopBreaksToSwitchBreaks(SequenceCompletions(section.Statements));

            // A break bound to this switch just continues after it.
            if ((sectionCompletion & TailCompletion.SwitchBreaks) != 0)
            {
                sectionCompletion = (sectionCompletion & ~TailCompletion.SwitchBreaks) | TailCompletion.FallsThrough;
            }

            result |= sectionCompletion;
        }

        if (!hasDefault)
        {
            result |= TailCompletion.FallsThrough;
        }

        return result;
    }

    /// <summary>Inside a switch section, a bare <c>break</c> binds to the switch, not the loop under analysis.</summary>
    private static TailCompletion MapLoopBreaksToSwitchBreaks(TailCompletion completion) =>
        (completion & TailCompletion.Breaks) != 0
            ? (completion & ~TailCompletion.Breaks) | TailCompletion.SwitchBreaks
            : completion;

    /// <summary>True when the block sits inside a try body somewhere between itself and the given loop body block.</summary>
    private static bool IsInsideTryBody(BlockSyntax block, BlockSyntax loopBody)
    {
        for (SyntaxNode? current = block; current is not null && !ReferenceEquals(current, loopBody); current = current.Parent)
        {
            if (current is BlockSyntax candidate && candidate.Parent is TryStatementSyntax tryStatement && ReferenceEquals(tryStatement.Block, candidate))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The body block of the innermost for/foreach/while/do enclosing (or equal to) this block, or null when the block isn't inside a loop.</summary>
    private static BlockSyntax? InnermostEnclosingLoopBody(BlockSyntax block)
    {
        for (SyntaxNode? current = block; current is not null; current = current.Parent)
        {
            var parentLoopBody = current.Parent switch
            {
                ForStatementSyntax forStatement => forStatement.Statement,
                ForEachStatementSyntax forEachStatement => forEachStatement.Statement,
                WhileStatementSyntax whileStatement => whileStatement.Statement,
                DoStatementSyntax doStatement => doStatement.Statement,
                _ => null
            };

            if (parentLoopBody is not null && ReferenceEquals(parentLoopBody, current))
            {
                return current as BlockSyntax;
            }
        }

        return null;
    }

    private static int CountAncestorLoops(SyntaxNode node) =>
        node.Ancestors().Count(a => a is ForStatementSyntax or ForEachStatementSyntax or WhileStatementSyntax or DoStatementSyntax);
}
