using Cmf.Cli.Plugin.CodeReview.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Cmf.Cli.Plugin.CodeReview;

/// <summary>
/// Parses the project's Web API controller(s) under Cmf.Custom.Business/Cmf.Custom.Services to build a map
/// of Orchestration method name -&gt; HTTP endpoint, by matching controller action methods back to their
/// [Http*]/[Route] attributes via the C# syntax tree. Every project names its controller differently
/// (e.g. ProjectController), so any *Controller.cs file in that folder is treated as a candidate.
/// </summary>
public static class CmServiceEndpointResolver
{
    private static readonly HashSet<string> HttpVerbAttributeNames = new(StringComparer.Ordinal)
    {
        "HttpGet", "HttpPost", "HttpPut", "HttpDelete", "HttpPatch"
    };

    /// <summary>Joins a base route and a relative segment into one normalized path. A leading-slash (absolute) segment replaces the base instead of appending to it.</summary>
    public static string JoinRoute(string baseRoute, string? segment)
    {
        if (string.IsNullOrWhiteSpace(segment))
        {
            return baseRoute;
        }

        if (segment.StartsWith('/'))
        {
            return segment.TrimStart('/');
        }

        return (baseRoute.TrimEnd('/') + "/" + segment.TrimStart('/')).Trim('/').Replace('\\', '/');
    }

    /// <summary>Maps each controller action method name to its resolved HTTP verb + route and source location, by matching action methods back to their [Http*]/[Route] attributes across every *Controller.cs file found.</summary>
    public static Dictionary<string, ServiceEndpointInfo> GetServiceEndpointMap(string repositoryRoot)
    {
        var map = new Dictionary<string, ServiceEndpointInfo>(StringComparer.Ordinal);
        var servicesDir = Path.Combine(repositoryRoot, "Cmf.Custom.Business", "Cmf.Custom.Services");
        if (!Directory.Exists(servicesDir))
        {
            return map;
        }

        foreach (var controllerPath in Directory.EnumerateFiles(servicesDir, "*Controller.cs", SearchOption.TopDirectoryOnly))
        {
            List<KeyValuePair<string, ServiceEndpointInfo>> endpoints;
            try
            {
                endpoints = GetServiceEndpointsFromController(repositoryRoot, controllerPath).ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // An unreadable controller only costs endpoint enrichment on its findings -- it
                // must not abort the scan. Stderr, because stdout is the pure-JSON contract.
                Console.Error.WriteLine($"warning: skipping unreadable controller file: {controllerPath} ({e.Message})");
                continue;
            }

            foreach (var (actionName, endpointInfo) in endpoints)
            {
                map[actionName] = endpointInfo;
            }
        }

        return map;
    }

    private static IEnumerable<KeyValuePair<string, ServiceEndpointInfo>> GetServiceEndpointsFromController(string repositoryRoot, string controllerPath)
    {
        var (_, root) = CodeReviewSyntaxTree.Parse(File.ReadAllText(controllerPath));
        var controllerRelativePath = CodeReviewPaths.GetRelativePath(repositoryRoot, controllerPath);

        var controllerClass = root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault();
        if (controllerClass is null)
        {
            yield break;
        }

        var classRouteTemplate = GetAttributeStringArgument(controllerClass.AttributeLists, "Route") ?? "api/[controller]/[action]";
        var controllerName = controllerClass.Identifier.Text;
        if (controllerName.EndsWith("Controller"))
        {
            controllerName = controllerName[..^"Controller".Length];
        }

        foreach (var method in controllerClass.Members.OfType<MethodDeclarationSyntax>())
        {
            if (!method.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)))
            {
                continue;
            }

            var actionName = method.Identifier.Text;
            var verb = "POST";
            string? actionTemplate = null;
            string? methodRoute = null;

            foreach (var attributeList in method.AttributeLists)
            {
                foreach (var attribute in attributeList.Attributes)
                {
                    var attributeName = attribute.Name.ToString();
                    if (HttpVerbAttributeNames.Contains(attributeName))
                    {
                        verb = attributeName["Http".Length..].ToUpperInvariant();
                        actionTemplate ??= GetAttributeStringArgument(attribute);
                    }
                    else if (attributeName == "Route")
                    {
                        methodRoute = GetAttributeStringArgument(attribute);
                    }
                }
            }

            var baseRoute = classRouteTemplate.Replace("[controller]", controllerName).Replace("[action]", actionName);
            string resolvedRoute;
            if (methodRoute is not null)
            {
                resolvedRoute = JoinRoute(baseRoute, methodRoute);
            }
            else if (actionTemplate is not null)
            {
                resolvedRoute = classRouteTemplate.Contains("[action]")
                    ? classRouteTemplate.Replace("[controller]", controllerName).Replace("[action]", actionTemplate)
                    : JoinRoute(baseRoute, actionTemplate);
            }
            else
            {
                resolvedRoute = baseRoute;
            }

            resolvedRoute = resolvedRoute.Trim('/').Replace('\\', '/');
            var signatureLine = method.Modifiers.First().GetLocation().GetLineSpan().StartLinePosition.Line + 1;

            yield return new KeyValuePair<string, ServiceEndpointInfo>(actionName, new ServiceEndpointInfo
            {
                Endpoint = $"{verb} /{resolvedRoute}",
                SourceLocation = $"{controllerRelativePath}:{signatureLine}"
            });
        }
    }

    private static string? GetAttributeStringArgument(SyntaxList<AttributeListSyntax> attributeLists, string attributeName) =>
        attributeLists
            .SelectMany(list => list.Attributes)
            .Where(attribute => attribute.Name.ToString() == attributeName)
            .Select(GetAttributeStringArgument)
            .FirstOrDefault(value => value is not null);

    private static string? GetAttributeStringArgument(AttributeSyntax attribute)
    {
        var argument = attribute.ArgumentList?.Arguments.FirstOrDefault();
        return argument?.Expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression)
            ? literal.Token.ValueText
            : null;
    }
}
