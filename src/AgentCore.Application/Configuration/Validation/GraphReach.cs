using AgentCore.Application.Configuration.Schema;

namespace AgentCore.Application.Configuration.Validation
{
    /// <summary>Forward adjacency and reachability over a policy or a graph, shared by checks 6 and 7.</summary>
    internal static class GraphReach
    {
        public static Dictionary<string, List<string>> BuildAdjacency(GraphConfiguration graph)
        {
            Dictionary<string, List<string>> forward = new(StringComparer.Ordinal);
            foreach (GraphNodeConfiguration node in graph.Nodes)
            {
                forward[node.Id] = [];
            }

            foreach (GraphEdgeConfiguration edge in graph.Edges)
            {
                if (forward.TryGetValue(edge.From, out List<string>? targets))
                {
                    targets.Add(edge.To);
                }
            }

            return forward;
        }

        /// <summary>Every id reachable from <paramref name="root"/>, including the root itself.</summary>
        public static HashSet<string> Reach(string root, Dictionary<string, List<string>> edges)
        {
            HashSet<string> seen = new(StringComparer.Ordinal) { root };
            Stack<string> pending = new();
            pending.Push(root);

            while (pending.Count > 0)
            {
                string current = pending.Pop();
                if (!edges.TryGetValue(current, out List<string>? targets))
                {
                    continue;
                }

                foreach (string? target in targets.Where(target => !seen.Contains(target)))
                {
                    _ = seen.Add(target);
                    pending.Push(target);
                }
            }

            return seen;
        }
    }
}
