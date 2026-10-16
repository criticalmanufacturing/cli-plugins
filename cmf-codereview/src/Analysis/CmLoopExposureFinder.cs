using Cmf.Cli.Plugin.CodeReview.Models;

namespace Cmf.Cli.Plugin.CodeReview;

/// <summary>
/// Walks a method's caller chain (breadth-first, bounded depth) looking for a call site
/// that is itself inside a loop -- i.e. "this method runs once per call, but the call
/// itself repeats because some ancestor caller loops over it".
/// </summary>
public static class CmLoopExposureFinder
{
    private class QueueItem
    {
        public required string MethodName { get; init; }
        public required List<LoopExposureHop> Path { get; init; }
        public int Depth { get; init; }
    }

    public static LoopExposure? FindLoopExposure(MethodCallGraph callGraph, string methodName, int maxDepth = 3)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal) { methodName };
        var queue = new Queue<QueueItem>();
        queue.Enqueue(new QueueItem { MethodName = methodName, Path = new List<LoopExposureHop>(), Depth = 0 });

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current.Depth >= maxDepth)
            {
                continue;
            }

            if (!callGraph.CallersByMethod.TryGetValue(current.MethodName, out var callers))
            {
                continue;
            }

            foreach (var caller in callers)
            {
                var newPath = new List<LoopExposureHop>(current.Path)
                {
                    new()
                    {
                        CallerMethodName = caller.CallerMethodName,
                        CallerRelativePath = caller.CallerRelativePath,
                        CallerLine = caller.CallerLine
                    }
                };

                if (caller.IsInLoop)
                {
                    return new LoopExposure
                    {
                        Kind = LoopExposure.CalledFromLoopKind,
                        LoopLocation = $"{caller.CallerRelativePath}:{caller.CallerLine}",
                        LoopMethod = caller.CallerMethodName,
                        Depth = current.Depth + 1,
                        Path = newPath
                    };
                }

                if (!visited.Contains(caller.CallerMethodName))
                {
                    visited.Add(caller.CallerMethodName);
                    queue.Enqueue(new QueueItem { MethodName = caller.CallerMethodName, Path = newPath, Depth = current.Depth + 1 });
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Renders the full caller chain as a human-readable sentence, e.g.
    /// "'A' calls 'B' directly (not in a loop there) at file:line; which in turn is called by 'C' calls 'B' in a loop at file:line."
    /// </summary>
    public static string FormatChain(string methodName, LoopExposure loopExposure)
    {
        var hops = new List<string>();
        var previousMethodName = methodName;
        var lastIndex = loopExposure.Path.Count - 1;

        for (var i = 0; i <= lastIndex; i++)
        {
            var hop = loopExposure.Path[i];
            var qualifier = i == lastIndex ? "in a loop" : "directly (not in a loop there)";
            hops.Add($"'{hop.CallerMethodName}' calls '{previousMethodName}' {qualifier} at {hop.CallerRelativePath}:{hop.CallerLine}");
            previousMethodName = hop.CallerMethodName;
        }

        return string.Join("; which in turn is called by ", hops);
    }
}
