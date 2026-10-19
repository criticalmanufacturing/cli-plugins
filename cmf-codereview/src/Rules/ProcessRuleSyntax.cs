using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cmf.Cli.Plugin.CodeReview.Rules;

/// <summary>Bounded, single-class syntax analysis. Unknown helpers never count as proof of a guard.</summary>
internal sealed class ProcessRuleSyntax(ClassDeclarationSyntax @class, MethodDeclarationSyntax action)
{
    public static string Name(InvocationExpressionSyntax call) => call.Expression switch
    {
        SimpleNameSyntax n => n.Identifier.ValueText,
        _ => CodeReviewSyntaxTree.GetMemberName(call.Expression)?.Identifier.ValueText ?? string.Empty
    };

    public static ExpressionSyntax? Receiver(InvocationExpressionSyntax call) => CodeReviewSyntaxTree.GetMemberTarget(call.Expression);
    public static string Tokens(SyntaxNode? node) => node is null ? string.Empty : string.Concat(node.DescendantTokens().Select(t => t.Text));
    public static ExpressionSyntax Unwrap(ExpressionSyntax expr) => expr is ParenthesizedExpressionSyntax p ? Unwrap(p.Expression) : expr;

    // Local functions are analyzed only when reachable from DeeActionCode; dormant helper bodies and
    // callbacks are not deployment operations. Recursive cycles are bounded by the visited set.
    public IReadOnlyList<InvocationExpressionSyntax> Calls { get; } = ReachableCalls(action);

    private static IReadOnlyList<InvocationExpressionSyntax> ReachableCalls(MethodDeclarationSyntax method)
    {
        var result = DirectCalls(method).ToList();
        var visited = new HashSet<LocalFunctionStatementSyntax>();
        for (var i = 0; i < result.Count; i++)
        {
            var call = result[i];
            if (FindLocal(call) is { } local && visited.Add(local)) result.AddRange(DirectCalls(local));
        }
        return result;
    }

    public static IEnumerable<SyntaxNode> DirectNodes(SyntaxNode callable) =>
        callable.DescendantNodes(n => n == callable || n is not (LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax));

    private static IEnumerable<InvocationExpressionSyntax> DirectCalls(SyntaxNode callable) =>
        DirectNodes(callable).OfType<InvocationExpressionSyntax>();

    private static LocalFunctionStatementSyntax? FindLocal(InvocationExpressionSyntax call)
    {
        if (call.Expression is not SimpleNameSyntax simple) return null;
        foreach (var block in call.Ancestors().OfType<BlockSyntax>())
        {
            var candidates = block.Statements.OfType<LocalFunctionStatementSyntax>()
                .Where(l => l.Identifier.ValueText == simple.Identifier.ValueText).ToList();
            if (candidates.Count != 0) return candidates.Count == 1 ? candidates[0] : null;
        }
        return null;
    }

    public string Key(ExpressionSyntax? expression)
    {
        if (expression is null) return string.Empty;
        return StringValue(expression) is { } value ? "\"" + value + "\"" : Tokens(expression);
    }

    public string? StringValue(ExpressionSyntax expression, int depth = 0)
    {
        if (depth > 6) return null;
        expression = Unwrap(expression);
        if (expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression)) return literal.Token.ValueText;
        if (expression is not IdentifierNameSyntax identifier) return null;
        // Respect lexical scopes; an unrelated method's same-named constant is not evidence.
        foreach (var scope in expression.Ancestors().Where(n => n is BlockSyntax or ClassDeclarationSyntax))
        {
            var candidates = scope is BlockSyntax block
                ? block.Statements.OfType<LocalDeclarationStatementSyntax>().SelectMany(d => d.Declaration.Variables)
                : ((ClassDeclarationSyntax)scope).Members.OfType<FieldDeclarationSyntax>().Where(f => f.Modifiers.Any(SyntaxKind.ConstKeyword)).SelectMany(f => f.Declaration.Variables);
            var declarations = candidates.Where(d => d.Identifier.ValueText == identifier.Identifier.ValueText &&
                (scope is ClassDeclarationSyntax || d.SpanStart < expression.SpanStart)).ToList();
            if (declarations.Count == 0) continue;
            if (declarations.Count != 1 || declarations[0].Initializer is null) return null;
            var declaration = declarations[0];
            if (scope.DescendantNodes().OfType<AssignmentExpressionSyntax>().Any(a => a.SpanStart > declaration.SpanStart &&
                a.SpanStart < expression.SpanStart && Tokens(a.Left) == identifier.Identifier.ValueText)) return null;
            return StringValue(declaration.Initializer!.Value, depth + 1);
        }
        return null;
    }

    public string? TypeOf(ExpressionSyntax? expression)
    {
        if (expression is null) return null;
        expression = Unwrap(expression);
        if (expression is not IdentifierNameSyntax identifier) return null;
        foreach (var scope in expression.Ancestors())
        {
            if (scope is BlockSyntax block)
            {
                var declaration = block.Statements.OfType<LocalDeclarationStatementSyntax>().SelectMany(d => d.Declaration.Variables)
                    .FirstOrDefault(d => d.Identifier.ValueText == identifier.Identifier.ValueText);
                if (declaration is not null) return DeclarationType(declaration);
            }
            var parameters = scope switch
            {
                MethodDeclarationSyntax m => m.ParameterList.Parameters,
                LocalFunctionStatementSyntax l => l.ParameterList.Parameters,
                _ => default
            };
            var parameter = parameters.FirstOrDefault(p => p.Identifier.ValueText == identifier.Identifier.ValueText);
            if (parameter?.Type is { } type) return CodeReviewSyntaxTree.LastIdentifierSegment(type.ToString());
        }
        return null;
    }

    private static string? DeclarationType(VariableDeclaratorSyntax variable)
    {
        var declared = ((VariableDeclarationSyntax)variable.Parent!).Type;
        if (!declared.IsVar) return CodeReviewSyntaxTree.LastIdentifierSegment(declared.ToString());
        var initial = variable.Initializer?.Value;
        if (initial is InvocationExpressionSyntax invocation)
        {
            var generic = invocation.Expression as GenericNameSyntax ?? CodeReviewSyntaxTree.GetMemberName(invocation.Expression) as GenericNameSyntax;
            if (generic?.TypeArgumentList.Arguments.Count == 1 && Name(invocation) is "GetService" or "GetRequiredService" or "Create")
                return CodeReviewSyntaxTree.LastIdentifierSegment(generic.TypeArgumentList.Arguments[0].ToString());
        }
        return initial is ObjectCreationExpressionSyntax creation ? CodeReviewSyntaxTree.LastIdentifierSegment(creation.Type.ToString()) : null;
    }

    public ExpressionSyntax? AssignedValue(string target, string member, SyntaxNode before)
    {
        var matches = @class.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.SpanStart < before.SpanStart && Tokens(a.Left) == target + "." + member &&
                a.Ancestors().FirstOrDefault(n => n is MethodDeclarationSyntax or LocalFunctionStatementSyntax) ==
                before.Ancestors().FirstOrDefault(n => n is MethodDeclarationSyntax or LocalFunctionStatementSyntax)).ToList();
        return matches.Count == 1 ? matches[0].Right : null;
    }

    public bool Guarded(SyntaxNode operation, string target, string? property, bool exists, int depth = 0)
    {
        if (depth > 4) return false;
        bool Predicate(ExpressionSyntax expression, bool truth) => IsGuardPredicate(expression, truth, target, property, exists) &&
            !Calls.Any(c => c.SpanStart > expression.Span.End && c.SpanStart < operation.SpanStart &&
                Tokens(Receiver(c)) == target && Name(c) is "Create" or "Delete" or "Remove" or "Terminate") &&
            !@class.DescendantNodes().OfType<AssignmentExpressionSyntax>().Any(a => a.SpanStart > expression.Span.End &&
                a.SpanStart < operation.SpanStart && (Tokens(a.Left) == target || Tokens(a.Left) == target + ".Name"));
        if (ControlGuard(operation, Predicate)) return true;
        if (operation.Ancestors().OfType<LocalFunctionStatementSyntax>().FirstOrDefault() is not { } local) return false;
        var callers = Calls.Where(c => FindLocal(c) == local).ToList();
        return callers.Count > 0 && callers.All(c => Guarded(c, Substitute(target, local, c),
            property is null ? null : Substitute(property, local, c), exists, depth + 1));
    }

    private string Substitute(string key, LocalFunctionStatementSyntax local, InvocationExpressionSyntax call)
    {
        for (var i = 0; i < local.ParameterList.Parameters.Count && i < call.ArgumentList.Arguments.Count; i++)
        {
            if (key == local.ParameterList.Parameters[i].Identifier.ValueText) return Key(call.ArgumentList.Arguments[i].Expression);
        }
        return key;
    }

    public string? ParameterAlias(LocalFunctionStatementSyntax local, string target)
    {
        var callers = Calls.Where(c => FindLocal(c) == local).ToList();
        if (callers.Count == 0) return null;
        foreach (var parameter in local.ParameterList.Parameters.Where(p => p.Type?.ToString() == "IEntityType"))
            if (callers.All(c => Substitute(parameter.Identifier.ValueText, local, c) == target)) return parameter.Identifier.ValueText;
        return null;
    }

    private bool IsGuardPredicate(ExpressionSyntax expression, bool truth, string target, string? property, bool exists)
    {
        if (expression is InvocationExpressionSyntax call)
        {
            if (property is not null && Name(call) is "HasProperty" or "TryGetEntityTypeProperty")
                return Tokens(Receiver(call)) == target && call.ArgumentList.Arguments.Count > 0 &&
                    Key(call.ArgumentList.Arguments[0].Expression) == property && truth == exists;
            if (property is null && Name(call) is "ObjectExists" or "TryLoad") return Tokens(Receiver(call)) == target && truth == exists;
        }
        return false;
    }

    public bool TerminationGuarded(SyntaxNode operation, string target) => ControlGuard(operation, (expression, truth) =>
    {
        if (expression is BinaryExpressionSyntax binary)
        {
            var member = binary.Left as MemberAccessExpressionSyntax ?? binary.Right as MemberAccessExpressionSyntax;
            var other = member == binary.Left ? binary.Right : binary.Left;
            if (member is not null && Tokens(member.Expression) == target && member.Name.Identifier.ValueText == "UniversalState" &&
                Tokens(other).EndsWith(".Terminated", StringComparison.Ordinal))
                return binary.IsKind(SyntaxKind.NotEqualsExpression) && truth || binary.IsKind(SyntaxKind.EqualsExpression) && !truth;
        }
        return false;
    });

    public bool HasSkippedGuard(string target, string? property, bool exists) => @class.Members.OfType<MethodDeclarationSyntax>()
        .Where(m => m.Identifier.ValueText == "DeeTestCondition")
        .SelectMany(m => DirectNodes(m).OfType<ExpressionSyntax>())
        .Any(e => IsGuardPredicate(e, exists, target, property, exists));

    /// <summary>A repair must remove the old property whenever it exists, not only when an unrelated flag is true.</summary>
    public bool RepairConditions(SyntaxNode operation, string target, string property, string? collection = null)
    {
        bool Known(ExpressionSyntax expression, bool truth)
        {
            expression = Unwrap(expression);
            if (expression is PrefixUnaryExpressionSyntax negation && negation.IsKind(SyntaxKind.LogicalNotExpression)) return Known(negation.Operand, !truth);
            if (expression is BinaryExpressionSyntax binary)
            {
                if (binary.IsKind(SyntaxKind.LogicalAndExpression) && truth) return Known(binary.Left, true) && Known(binary.Right, true);
                if (collection is not null && Tokens(binary.Left) is { } left && (left == collection + ".Count" || left == collection + "?.Count") &&
                    binary.Right is LiteralExpressionSyntax { Token.ValueText: "0" } && truth &&
                    (binary.IsKind(SyntaxKind.GreaterThanExpression) || binary.IsKind(SyntaxKind.NotEqualsExpression))) return true;
            }
            return IsGuardPredicate(expression, truth, target, property, true) || IsGuardPredicate(expression, truth, target, null, true);
        }
        foreach (var ancestor in operation.Ancestors())
        {
            if (ancestor is MethodDeclarationSyntax or LocalFunctionStatementSyntax) break;
            if (ancestor is IfStatementSyntax condition && !Known(condition.Condition, condition.Statement.Span.Contains(operation.Span))) return false;
        }
        return true;
    }

    public static bool ControlGuard(SyntaxNode operation, Func<ExpressionSyntax, bool, bool> predicate)
    {
        for (SyntaxNode? node = operation; node?.Parent is { } parent; node = parent)
        {
            if (parent is LocalFunctionStatementSyntax or MethodDeclarationSyntax or AnonymousFunctionExpressionSyntax) break;
            if (parent is IfStatementSyntax condition && condition.Statement.Span.Contains(node.Span) && Guarantees(condition.Condition, true, predicate)) return true;
            if (parent is ElseClauseSyntax alternative && alternative.Parent is IfStatementSyntax other && Guarantees(other.Condition, false, predicate)) return true;
            if (parent is BlockSyntax block)
            {
                var containing = block.Statements.FirstOrDefault(s => s.Span.Contains(node.Span));
                foreach (var previous in block.Statements.TakeWhile(s => s != containing))
                {
                    if (previous is IfStatementSyntax early &&
                        (Exits(early.Statement) && Guarantees(early.Condition, false, predicate) ||
                         early.Else is { } @else && Exits(@else.Statement) && Guarantees(early.Condition, true, predicate))) return true;
                }
            }
        }
        return false;
    }

    private static bool Guarantees(ExpressionSyntax expression, bool truth, Func<ExpressionSyntax, bool, bool> predicate)
    {
        expression = Unwrap(expression);
        if (expression is PrefixUnaryExpressionSyntax negation && negation.IsKind(SyntaxKind.LogicalNotExpression)) return Guarantees(negation.Operand, !truth, predicate);
        if (expression is BinaryExpressionSyntax binary)
        {
            if (binary.IsKind(SyntaxKind.LogicalAndExpression)) return truth
                ? Guarantees(binary.Left, true, predicate) || Guarantees(binary.Right, true, predicate)
                : Guarantees(binary.Left, false, predicate) && Guarantees(binary.Right, false, predicate);
            if (binary.IsKind(SyntaxKind.LogicalOrExpression)) return truth
                ? Guarantees(binary.Left, true, predicate) && Guarantees(binary.Right, true, predicate)
                : Guarantees(binary.Left, false, predicate) || Guarantees(binary.Right, false, predicate);
            if (binary.IsKind(SyntaxKind.EqualsExpression) || binary.IsKind(SyntaxKind.NotEqualsExpression))
            {
                var literal = binary.Right as LiteralExpressionSyntax ?? binary.Left as LiteralExpressionSyntax;
                if (literal is not null && (literal.IsKind(SyntaxKind.TrueLiteralExpression) || literal.IsKind(SyntaxKind.FalseLiteralExpression)))
                {
                    var operand = literal == binary.Right ? binary.Left : binary.Right;
                    var desired = truth == binary.IsKind(SyntaxKind.EqualsExpression) == literal.IsKind(SyntaxKind.TrueLiteralExpression);
                    return Guarantees(operand, desired, predicate);
                }
            }
        }
        return predicate(expression, truth);
    }

    private static bool Exits(StatementSyntax statement) => statement switch
    {
        ReturnStatementSyntax or ThrowStatementSyntax => true,
        BlockSyntax block => block.Statements.Any(Exits),
        IfStatementSyntax conditional => Exits(conditional.Statement) && conditional.Else is { } other && Exits(other.Statement),
        _ => false
    };

    /// <summary>True for recognized calls that must occur after this operation in the same execution path.</summary>
    public bool HasFollowingCall(SyntaxNode operation, Func<InvocationExpressionSyntax, bool> predicate)
    {
        foreach (var block in operation.Ancestors().OfType<BlockSyntax>())
        {
            if (block.Ancestors().OfType<LocalFunctionStatementSyntax>().FirstOrDefault() != operation.Ancestors().OfType<LocalFunctionStatementSyntax>().FirstOrDefault()) break;
            var statement = block.Statements.FirstOrDefault(s => s.Span.Contains(operation.Span));
            if (statement is null) continue;
            foreach (var next in block.Statements.SkipWhile(s => s != statement).Skip(1))
            {
                if (next is ReturnStatementSyntax or ThrowStatementSyntax) return false;
                if (DirectNodes(next).Any(n => n is ReturnStatementSyntax or ThrowStatementSyntax)) return false;
                if (next is ExpressionStatementSyntax or LocalDeclarationStatementSyntax)
                    if (DirectCalls(next).Any(predicate)) return true;
            }
        }
        return false;
    }

    public bool CleanupMatches(InvocationExpressionSyntax call, string property) => Name(call) == "RemoveAttributes" &&
        call.ArgumentList.Arguments.Any(a => ContainsProperty(a.Expression, property));

    private bool ContainsProperty(ExpressionSyntax expression, string property)
    {
        if (expression.DescendantNodesAndSelf().OfType<ExpressionSyntax>().Any(e => Key(e) == property)) return true;
        if (expression is not IdentifierNameSyntax identifier) return false;
        var variables = expression.Ancestors().OfType<BlockSyntax>().SelectMany(b => b.Statements.OfType<LocalDeclarationStatementSyntax>())
            .SelectMany(d => d.Declaration.Variables).Where(v => v.Identifier.ValueText == identifier.Identifier.ValueText).ToList();
        return variables.Count == 1 && variables[0].Initializer?.Value is BaseObjectCreationExpressionSyntax creation &&
            creation.Initializer?.Expressions.Any(e => Key(e) == property) == true;
    }

    public bool HasPriorCleanup(SyntaxNode operation, string property) => Calls.Any(c => CleanupMatches(c, property) &&
        c.SpanStart < operation.SpanStart &&
        SameExecutionScope(c, operation));

    private static bool SameExecutionScope(SyntaxNode earlier, SyntaxNode later)
    {
        var earlierMethod = earlier.Ancestors().FirstOrDefault(n => n is LocalFunctionStatementSyntax or MethodDeclarationSyntax);
        if (earlierMethod != later.Ancestors().FirstOrDefault(n => n is LocalFunctionStatementSyntax or MethodDeclarationSyntax)) return false;
        if (earlier.Ancestors().OfType<TryStatementSyntax>().Any(t => t.Catches.Count > 0 &&
            t.Block.Span.Contains(earlier.Span) && !t.Block.Span.Contains(later.Span))) return false;
        // Conditional cleanup counts only when the removal is in that same branch. A loop over holders
        // may be empty: in that case there was no value to clear, so it is a valid cleanup shape.
        return earlier.Ancestors().OfType<IfStatementSyntax>().All(i =>
            i.Statement.Span.Contains(earlier.Span) ? i.Statement.Span.Contains(later.Span) : i.Else?.Span.Contains(later.Span) == true);
    }
}
