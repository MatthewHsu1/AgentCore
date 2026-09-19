using AgentCore.Application.Configuration.Schema;

namespace AgentCore.Application.Configuration.Validation;

/// <summary>Forward adjacency and reachability over a policy or a graph, shared by checks 6 and 7.</summary>
internal static class GraphReach
{
    public static Dictionary<string, List<string>> BuildAdjacency(GraphConfiguration graph)
    {
        var forward = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var node in graph.Nodes)
        {
            forward[node.Id] = [];
        }

        foreach (var edge in graph.Edges)
        {
            if (forward.TryGetValue(edge.From, out var targets))
            {
                targets.Add(edge.To);
            }
        }

        return forward;
    }

    /// <summary>Every id reachable from <paramref name="root"/>, including the root itself.</summary>
    public static HashSet<string> Reach(string root, Dictionary<string, List<string>> edges)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { root };
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!edges.TryGetValue(current, out var targets))
            {
                continue;
            }

            foreach (var target in targets.Where(target => !seen.Contains(target)))
            {
                seen.Add(target);
                pending.Push(target);
            }
        }

        return seen;
    }
}
