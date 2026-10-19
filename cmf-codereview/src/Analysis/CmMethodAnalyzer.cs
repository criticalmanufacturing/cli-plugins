using Cmf.Cli.Plugin.CodeReview.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cmf.Cli.Plugin.CodeReview;

/// <summary>
/// Detects method (and local function) boundaries via the C# syntax tree, used to attribute a
/// given line to its enclosing method name.
/// </summary>
public static class CmMethodAnalyzer
{
    /// <summary>Detects method/local-function signature/body boundaries in a file.</summary>
    public static List<MethodRange> GetMethodRanges(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0)
        {
            return new List<MethodRange>();
        }

        var (_, root) = CodeReviewSyntaxTree.Parse(lines);
        return GetMethodRanges(root);
    }

    /// <summary>Same analysis over an already-parsed root, so cached parses aren't re-done.</summary>
    public static List<MethodRange> GetMethodRanges(CompilationUnitSyntax root)
    {
        var ranges = new List<MethodRange>();

        foreach (var node in root.DescendantNodes())
        {
            string name;
            SyntaxNode? bodyNode;

            switch (node)
            {
                case MethodDeclarationSyntax method:
                    name = method.Identifier.Text;
                    bodyNode = (SyntaxNode?)method.Body ?? method.ExpressionBody;
                    break;
                case LocalFunctionStatementSyntax localFunction:
                    name = localFunction.Identifier.Text;
                    bodyNode = (SyntaxNode?)localFunction.Body ?? localFunction.ExpressionBody;
                    break;
                default:
                    continue;
            }

            if (bodyNode is null)
            {
                continue;
            }

            ranges.Add(new MethodRange
            {
                MethodName = name,
                StartLine = CodeReviewSyntaxTree.StartLine(bodyNode),
                StartDepth = 0,
                EndLine = CodeReviewSyntaxTree.EndLine(bodyNode)
            });
        }

        return ranges;
    }

    /// <summary>Picks the innermost (highest StartLine) method range containing the given line.</summary>
    public static string? GetMethodNameForLine(IReadOnlyList<MethodRange> methodRanges, int lineNumber)
    {
        MethodRange? candidate = null;
        foreach (var range in methodRanges)
        {
            if (lineNumber >= range.StartLine && lineNumber <= range.EndLine)
            {
                if (candidate is null || range.StartLine > candidate.StartLine)
                {
                    candidate = range;
                }
            }
        }

        return candidate?.MethodName;
    }
}
