using Cmf.Cli.Plugin.CodeReview.Enums;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cmf.Cli.Plugin.CodeReview;

/// <summary>A DEE Action Group wiring removed by a ProcessRule migration script. <see cref="ActionGroup"/> is null when every Action Group wiring for <see cref="Dee"/> was removed, rather than just one.</summary>
public sealed class DeeActionGroupRemoval
{
    public required string Dee { get; init; }
    public string? ActionGroup { get; init; }
}

/// <summary>Recognizes single-DEE removal scripts with literal names in versioned ProcessRules folders.</summary>
public static class DeeActionGroupRemovalScanner
{
    public static List<DeeActionGroupRemoval> ScanRemovals(string repositoryRoot, CodeReviewFileCache? fileCache = null)
    {
        fileCache ??= new CodeReviewFileCache();
        var files = CodeReviewPaths
            .GetFiles(repositoryRoot, CodeReviewPaths.DiscoverDeeContentRoots(repositoryRoot))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(CodeReviewPaths.IsOneTimeMigrationFile);

        var removals = new List<DeeActionGroupRemoval>();
        foreach (var file in files)
        {
            removals.AddRange(ScanFile(fileCache.Get(file).Root));
        }

        return removals;
    }

    private static IEnumerable<DeeActionGroupRemoval> ScanFile(SyntaxNode root)
    {
        var literals = CollectStringLiterals(root);
        var dee = ResolveSingleActionName(root, literals);
        if (dee is null)
        {
            yield break;
        }

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var methodName = GetInvokedMethodName(invocation);

            if (methodName == "RemoveAction" && IsWholeDeeRemoval(invocation))
            {
                yield return new DeeActionGroupRemoval { Dee = dee, ActionGroup = null };
                continue;
            }

            if (methodName != "RemoveActionGroups" || invocation.ArgumentList.Arguments.Count != 1)
            {
                continue;
            }

            var argument = invocation.ArgumentList.Arguments[0].Expression;
            if (argument is MemberAccessExpressionSyntax { Name.Identifier.Text: "ActionGroupActions" })
            {
                yield return new DeeActionGroupRemoval { Dee = dee, ActionGroup = null };
                continue;
            }

            var actionGroup = FindFilteredActionGroup(argument, invocation, literals);
            if (actionGroup is not null)
            {
                yield return new DeeActionGroupRemoval { Dee = dee, ActionGroup = actionGroup };
            }
        }
    }

    private static Dictionary<string, string> CollectStringLiterals(SyntaxNode root)
    {
        var literals = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var declarator in root.DescendantNodes().OfType<VariableDeclaratorSyntax>())
        {
            if (declarator.Initializer?.Value is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
            {
                literals[declarator.Identifier.Text] = literal.Token.ValueText;
            }
        }

        return literals;
    }

    /// <summary>
    /// The single DEE name this file's "&lt;ident&gt;.Name = &lt;value&gt;" assignments resolve to -- null
    /// if none, or more than one distinct name, is found (see class remarks for why ambiguity means skip
    /// rather than guess).
    /// </summary>
    private static string? ResolveSingleActionName(SyntaxNode root, Dictionary<string, string> literals)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var assignment in root.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            if (assignment.Left is MemberAccessExpressionSyntax { Name.Identifier.Text: "Name" } &&
                TryResolveLiteral(assignment.Right, literals, out var value))
            {
                names.Add(value);
            }
        }

        return names.Count == 1 ? names.First() : null;
    }

    private static bool TryResolveLiteral(ExpressionSyntax expression, Dictionary<string, string> literals, out string value)
    {
        if (expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
        {
            value = literal.Token.ValueText;
            return true;
        }

        if (expression is IdentifierNameSyntax ident && literals.TryGetValue(ident.Identifier.Text, out var resolved))
        {
            value = resolved;
            return true;
        }

        value = "";
        return false;
    }

    private static bool IsWholeDeeRemoval(InvocationExpressionSyntax invocation)
    {
        foreach (var arg in invocation.ArgumentList.Arguments)
        {
            if (arg.Expression is BaseObjectCreationExpressionSyntax { Initializer: { } initializer } &&
                initializer.Expressions.OfType<AssignmentExpressionSyntax>().Any(a =>
                    a.Left is IdentifierNameSyntax { Identifier.Text: "IsToRemoveAllVersions" } &&
                    a.Right.IsKind(SyntaxKind.TrueLiteralExpression)))
            {
                return true;
            }
        }

        return false;
    }

    // Follow only the collection supplied to this removal; unrelated filters cannot remove wiring.
    private static string? FindFilteredActionGroup(ExpressionSyntax argument, InvocationExpressionSyntax removal,
        Dictionary<string, string> literals)
    {
        var sources = new List<ExpressionSyntax> { argument };
        if (argument is IdentifierNameSyntax identifier)
        {
            var scope = removal.Ancestors().FirstOrDefault(n => n is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax);
            if (scope is null) return null;
            var name = identifier.Identifier.ValueText;
            sources.AddRange(scope.DescendantNodes().OfType<VariableDeclaratorSyntax>()
                .Where(v => v.SpanStart < removal.SpanStart && v.Identifier.ValueText == name && v.Initializer is not null)
                .Select(v => v.Initializer!.Value));
            sources.AddRange(scope.DescendantNodes().OfType<AssignmentExpressionSyntax>()
                .Where(a => a.SpanStart < removal.SpanStart && a.Left is IdentifierNameSyntax id && id.Identifier.ValueText == name)
                .Select(a => a.Right));
            sources.AddRange(scope.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(c => c.SpanStart < removal.SpanStart && c.Expression is MemberAccessExpressionSyntax
                    { Expression: IdentifierNameSyntax id, Name.Identifier.ValueText: "AddRange" } && id.Identifier.ValueText == name)
                .SelectMany(c => c.ArgumentList.Arguments.Select(a => a.Expression)));
        }
        var groups = new HashSet<string>(StringComparer.Ordinal);
        foreach (var invocation in sources.SelectMany(s => s.DescendantNodesAndSelf()).OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is MemberAccessExpressionSyntax
                {
                    Name.Identifier.Text: "Equals",
                    Expression: MemberAccessExpressionSyntax
                    {
                        Name.Identifier.Text: "Name",
                        Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "ActionGroup" }
                    }
                } && invocation.ArgumentList.Arguments.Count == 1 &&
                TryResolveLiteral(invocation.ArgumentList.Arguments[0].Expression, literals, out var actionGroup)) groups.Add(actionGroup);
        }
        return groups.Count == 1 ? groups.Single() : null;
    }

    private static string? GetInvokedMethodName(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax member => member.Name.Identifier.Text,
        IdentifierNameSyntax ident => ident.Identifier.Text,
        _ => null
    };
}
