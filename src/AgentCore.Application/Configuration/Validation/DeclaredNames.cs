using AgentCore.Application.Configuration.Schema;

namespace AgentCore.Application.Configuration.Validation
{
    /// <summary>Every name the document declares, indexed once so each reference check resolves in O(1).</summary>
    internal sealed class DeclaredNames
    {
        public required HashSet<string> Agents { get; init; }

        public required HashSet<string> Guards { get; init; }

        public required HashSet<string> Models { get; init; }

        /// <summary>Stage ids of each entry's policy, keyed by entry name.</summary>
        public required Dictionary<string, HashSet<string>> Stages { get; init; }

        /// <summary>Node ids of each entry's graph, keyed by entry name.</summary>
        public required Dictionary<string, HashSet<string>> Nodes { get; init; }

        public static DeclaredNames From(AgentCoreConfiguration configuration)
        {
            return new()
            {
                Agents = configuration.Agents.Items
                            .Select(static agent => agent.Id).ToHashSet(StringComparer.Ordinal),
                Guards = configuration.Guards.Keys.ToHashSet(StringComparer.Ordinal),
                Stages = configuration.Entries.ToDictionary(
                            static entry => entry.Key,
                            entry => (entry.Value.Policy?.Stages ?? [])
                                .Select(static stage => stage.Id).ToHashSet(StringComparer.Ordinal),
                            StringComparer.Ordinal),
                Nodes = configuration.Entries.ToDictionary(
                            static entry => entry.Key,
                            entry => (entry.Value.Graph?.Nodes ?? [])
                                .Select(static node => node.Id).ToHashSet(StringComparer.Ordinal),
                            StringComparer.Ordinal),
                Models = (configuration.Providers?.Llm ?? [])
                            .Select(static provider => provider.As).ToHashSet(StringComparer.Ordinal),
            };
        }
    }
}
