using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using AgentCore.Application.Runtime.Agents.Graph;

namespace AgentCore.Application.Configuration.Compilation
{
    /// <summary>
    /// Entry holds <c>graph:</c> with <c>pattern:</c>. It builds
    /// <c>AgentWorkflowBuilder.BuildSequential</c>, <c>BuildConcurrent</c>,
    /// <c>CreateHandoffBuilderWith</c>, or <c>CreateGroupChatBuilderWith</c>.
    /// </summary>
    internal sealed class PatternGraphRow : CompileTableRow
    {
        internal static readonly PatternGraphRow Instance = new();

        internal override CompiledAgentShape Shape => CompiledAgentShape.PatternGraph;

        internal override EntryBuild BuildEntry(
            AgentCoreConfiguration configuration,
            string entryName,
            EntryConfiguration entry,
            string entryPointer,
            Dictionary<string, AIAgent> agents,
            AgentCompilationContext context,
            bool reusesGraphSession)
        {
            GraphConfiguration graph = entry.Graph!;
            string graphPointer = ConfigurationError.AppendPointer(entryPointer, "graph");
            string agentsPointer = ConfigurationError.AppendPointer(graphPointer, "agents");
            List<AIAgent> participants = [];

            for (int index = 0; index < graph.Agents.Count; index++)
            {
                string id = graph.Agents[index];
                if (!agents.TryGetValue(id, out AIAgent? agent))
                {
                    throw ConfigurationCompiler.Fail(
                        ConfigurationError.AppendPointer(agentsPointer, index),
                        $"the graph names the agent '{id}', which agents.items does not declare.");
                }

                if (ConfigurationCompiler.AgentDeclaresBackground(configuration, id))
                {
                    throw ConfigurationCompiler.FailBackgroundInGraph(ConfigurationError.AppendPointer(agentsPointer, index), id, entryName);
                }

                participants.Add(new GraphParticipantAgent(agent));
            }

            if (participants.Count == 0)
            {
                throw ConfigurationCompiler.Fail(agentsPointer, "a pattern graph names no agent.");
            }

            Workflow workflow = graph.Pattern switch
            {
                GraphPattern.Sequential => AgentWorkflowBuilder.BuildSequential(entryName, participants),
                GraphPattern.Concurrent => AgentWorkflowBuilder.BuildConcurrent(entryName, participants, aggregator: null),
                GraphPattern.Handoff => BuildHandoff(entryName, participants),
                GraphPattern.GroupChat => BuildGroupChat(entryName, participants),
                _ => throw new ArgumentOutOfRangeException(nameof(entry), graph.Pattern, "The graph pattern vocabulary is closed, and this value is not in it."),
            };

            return new EntryBuild(new GraphFaultAgent(workflow.AsAIAgent(name: entryName), drain: reusesGraphSession), NoStages());
        }

        internal override HashSet<string>? SpokenAuthors(AgentCoreConfiguration configuration, EntryConfiguration entry)
        {
            return entry.Graph is { Pattern: GraphPattern.Sequential, Agents.Count: > 0 } graph
                        ? new HashSet<string>(StringComparer.Ordinal) { graph.Agents[^1] }
                        : null;
        }

        private static Workflow BuildHandoff(string name, List<AIAgent> participants)
        {
            AIAgent start = participants[0];
            HandoffWorkflowBuilder builder = AgentWorkflowBuilder.CreateHandoffBuilderWith(start).WithName(name);

            if (participants.Count > 1)
            {
                builder = builder.WithHandoffs(start, participants.Skip(1));
            }

            return builder.Build();
        }

        private static Workflow BuildGroupChat(string name, List<AIAgent> participants)
        {
            return AgentWorkflowBuilder
                        .CreateGroupChatBuilderWith(members => new RoundRobinGroupChatManager(members))
                        .AddParticipants(participants)
                        .WithName(name)
                        .Build();
        }
    }
}
