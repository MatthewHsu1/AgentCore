using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;

namespace AgentCore.Application.Configuration.Compilation;

/// <summary>
/// Row 3: <c>graph:</c> with <c>pattern:</c>. It builds <c>AgentWorkflowBuilder.BuildSequential</c>,
/// <c>BuildConcurrent</c>, <c>CreateHandoffBuilderWith</c>, or <c>CreateGroupChatBuilderWith</c>.
/// </summary>
internal sealed class PatternGraphRow : CompileTableRow
{
    internal static readonly PatternGraphRow Instance = new();

    internal override CompiledAgentShape Shape => CompiledAgentShape.PatternGraph;

    internal override (AIAgent Entry, Dictionary<string, string> Stages) BuildEntry(
        AgentCoreConfiguration configuration,
        Dictionary<string, AIAgent> agents,
        AgentCompilationContext context)
    {
        var graph = configuration.Graph!;
        List<AIAgent> participants = [];

        for (var index = 0; index < graph.Agents.Count; index++)
        {
            var id = graph.Agents[index];
            if (!agents.TryGetValue(id, out var agent))
            {
                throw ConfigurationCompiler.Fail(
                    ConfigurationError.AppendPointer("/graph/agents", index),
                    $"the graph names the agent '{id}', which agents.items does not declare.");
            }

            participants.Add(agent);
        }

        if (participants.Count == 0)
        {
            throw ConfigurationCompiler.Fail("/graph/agents", "a pattern graph names no agent.");
        }

        var workflow = graph.Pattern switch
        {
            GraphPattern.Sequential => AgentWorkflowBuilder.BuildSequential(configuration.Name, participants),
            GraphPattern.Concurrent => AgentWorkflowBuilder.BuildConcurrent(configuration.Name, participants, aggregator: null),
            GraphPattern.Handoff => BuildHandoff(configuration.Name, participants),
            _ => BuildGroupChat(configuration.Name, participants),
        };

        return (workflow.AsAIAgent(name: configuration.Name), NoStages());
    }

    /// <remarks>
    /// A sequential graph answers from its last participant. Concurrent aggregates every
    /// participant, and handoff and group chat both end wherever the conversation took them, so on
    /// those patterns any participant may legitimately speak last and no filter applies.
    /// </remarks>
    internal override HashSet<string>? SpokenAuthors(AgentCoreConfiguration configuration)
        => configuration.Graph is { Pattern: GraphPattern.Sequential, Agents.Count: > 0 } graph
            ? new HashSet<string>(StringComparer.Ordinal) { graph.Agents[^1] }
            : null;

    private static Workflow BuildHandoff(string name, List<AIAgent> participants)
    {
        var start = participants[0];
        var builder = AgentWorkflowBuilder.CreateHandoffBuilderWith(start).WithName(name);

        if (participants.Count > 1)
        {
            builder = builder.WithHandoffs(start, participants.Skip(1));
        }

        return builder.Build();
    }

    private static Workflow BuildGroupChat(string name, List<AIAgent> participants)
        => AgentWorkflowBuilder
            .CreateGroupChatBuilderWith(members => new RoundRobinGroupChatManager(members))
            .AddParticipants(participants)
            .WithName(name)
            .Build();
}
