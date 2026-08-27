using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Runtime;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;

namespace AgentCore.Application.Configuration.Compilation;

/// <summary>
/// Row 4: <c>graph:</c> with <c>nodes:</c> and <c>edges:</c>. It builds a <c>WorkflowBuilder</c>,
/// binds the agents as executors, then <c>AsAIAgent()</c>.
/// </summary>
internal sealed class ExplicitGraphRow : CompileTableRow
{
    internal static readonly ExplicitGraphRow Instance = new();

    internal override CompiledAgentShape Shape => CompiledAgentShape.ExplicitGraph;

    internal override (AIAgent Entry, Dictionary<string, string> Stages) BuildEntry(
        AgentCoreConfiguration configuration,
        Dictionary<string, AIAgent> agents,
        AgentCompilationContext context)
    {
        var graph = configuration.Graph!;

        Dictionary<string, ExecutorBinding> nodes = new(StringComparer.Ordinal);
        List<GraphNodeConfiguration> starts = [];
        List<ExecutorBinding> outputs = [];

        for (var index = 0; index < graph.Nodes.Count; index++)
        {
            var node = graph.Nodes[index];
            var pointer = ConfigurationError.AppendPointer("/graph/nodes", index);

            if (node.Agent is not { } agentId || !agents.TryGetValue(agentId, out var agent))
            {
                throw ConfigurationCompiler.Fail(
                    ConfigurationError.AppendPointer(pointer, "agent"),
                    $"the node '{node.Id}' names no declared agent, so nothing binds as its executor.");
            }

            if (nodes.ContainsKey(node.Id))
            {
                throw ConfigurationCompiler.Fail(
                    ConfigurationError.AppendPointer(pointer, "id"),
                    $"the node id '{node.Id}' is declared twice.");
            }

            // The measured shape: an agent binds as an executor, and the host emits update events so
            // the wrapper can stream. Section 8.6.
            var binding = new AIAgentBinding(agent, new AIAgentHostOptions { EmitAgentUpdateEvents = true });
            nodes[node.Id] = binding;

            if (node.Start)
            {
                starts.Add(node);
            }

            if (node.Output)
            {
                outputs.Add(binding);
            }
        }

        if (starts.Count != 1)
        {
            throw ConfigurationCompiler.Fail(
                "/graph/nodes",
                $"the graph declares {starts.Count} start nodes. Check 7 of section 8.5 needs exactly one.");
        }

        WorkflowBuilder builder = new(nodes[starts[0].Id]);
        Func<IReadOnlyDictionary<string, JsonNode?>>? guardedState = null;

        for (var index = 0; index < graph.Edges.Count; index++)
        {
            var edge = graph.Edges[index];
            var pointer = ConfigurationError.AppendPointer("/graph/edges", index);

            if (!nodes.TryGetValue(edge.From, out var from))
            {
                throw ConfigurationCompiler.Fail(
                    ConfigurationError.AppendPointer(pointer, "from"),
                    $"the node '{edge.From}' is not declared.");
            }

            if (!nodes.TryGetValue(edge.To, out var to))
            {
                throw ConfigurationCompiler.Fail(
                    ConfigurationError.AppendPointer(pointer, "to"),
                    $"the node '{edge.To}' is not declared.");
            }

            if (edge.When is not { } guard)
            {
                builder = builder.AddEdge(from, to);
                continue;
            }

            if (context.Guards is not { } evaluator || context.StateSnapshot is not { } snapshot)
            {
                throw ConfigurationCompiler.Fail(
                    ConfigurationError.AppendPointer(pointer, "when"),
                    $"the edge carries a guard, and the compilation context binds {Missing(context)}. Bind "
                    + "both: AgentCompilationContext.Guards runs the rule, and "
                    + "AgentCompilationContext.StateSnapshot reads the state of the call that runs now. "
                    + "AddAgentCore binds them to GuardEvaluator and to CallStateScope.Snapshot. A guarded "
                    + "edge that silently became unconditional is exactly the silent graph failure "
                    + "section 8.2 refuses to ship.");
            }

            // The predicate captures no state of its own. The compiled graph is a process singleton
            // under T44, so it asks the context for the state of the call that runs now. object, not
            // List<ChatMessage>: the guard reads state, so every message on the edge takes the same
            // answer and the turn token is not filtered out.
            builder = builder.AddEdge<object>(from, to, _ => evaluator.Evaluate(guard, snapshot()));
            guardedState = snapshot;
        }

        if (outputs.Count > 0)
        {
            builder = builder.WithOutputFrom([.. outputs]);
        }

        var compiled = builder.WithName(configuration.Name)
                              .Build()
                              .AsAIAgent(name: configuration.Name);

        AIAgent withOutputCheck = new RequireOutputAgent(compiled, configuration.Name);
        return (
            guardedState is null ? withOutputCheck : new RequireStateAgent(withOutputCheck, guardedState),
            NoStages());
    }

    /// <remarks>An explicit graph answers from its <c>output: true</c> nodes.</remarks>
    internal override HashSet<string>? SpokenAuthors(AgentCoreConfiguration configuration)
    {
        HashSet<string> outputs = new(StringComparer.Ordinal);
        foreach (var node in configuration.Graph!.Nodes)
        {
            if (node.Output && node.Agent is { } agentId)
            {
                outputs.Add(agentId);
            }
        }

        return outputs.Count == 0 ? null : outputs;
    }

    /// <summary>Names the seams a guarded edge needs and the context left unbound.</summary>
    /// <param name="context">The seams the document names.</param>
    /// <returns>The phrase the failure message carries.</returns>
    private static string Missing(AgentCompilationContext context) => (context.Guards, context.StateSnapshot) switch
    {
        (null, null) => "no guard evaluator and no state source",
        (null, _) => "no guard evaluator",
        _ => "no state source",
    };
}
