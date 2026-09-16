using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Runtime;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;

namespace AgentCore.Application.Configuration.Compilation;

/// <summary>
/// Row 4: the entry holds <c>graph:</c> with <c>nodes:</c> and <c>edges:</c>. It builds a
/// <c>WorkflowBuilder</c>, binds the agents as executors, then <c>AsAIAgent()</c>.
/// </summary>
internal sealed class ExplicitGraphRow : CompileTableRow
{
    internal static readonly ExplicitGraphRow Instance = new();

    internal override CompiledAgentShape Shape => CompiledAgentShape.ExplicitGraph;

    internal override (AIAgent Entry, Dictionary<string, string> Stages) BuildEntry(
        AgentCoreConfiguration configuration,
        string entryName,
        EntryConfiguration entry,
        string entryPointer,
        Dictionary<string, AIAgent> agents,
        AgentCompilationContext context)
    {
        var graph = entry.Graph!;
        var graphPointer = ConfigurationError.AppendPointer(entryPointer, "graph");
        var nodesPointer = ConfigurationError.AppendPointer(graphPointer, "nodes");
        var edgesPointer = ConfigurationError.AppendPointer(graphPointer, "edges");

        Dictionary<string, ExecutorBinding> nodes = new(StringComparer.Ordinal);
        List<GraphNodeConfiguration> starts = [];
        List<ExecutorBinding> outputs = [];

        for (var index = 0; index < graph.Nodes.Count; index++)
        {
            var node = graph.Nodes[index];
            var pointer = ConfigurationError.AppendPointer(nodesPointer, index);

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
                nodesPointer,
                $"the graph declares {starts.Count} start nodes. Check 7 of section 8.5 needs exactly one.");
        }

        var stateEntry = ExecutorBindingExtensions.BindExecutor(new GraphStateEntry());

        WorkflowBuilder builder = new(stateEntry);
        builder = builder.AddEdge(stateEntry, nodes[starts[0].Id]);

        var guarded = false;

        for (var index = 0; index < graph.Edges.Count; index++)
        {
            var edge = graph.Edges[index];
            var pointer = ConfigurationError.AppendPointer(edgesPointer, index);

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

            if (context.Guards is not { } evaluator)
            {
                throw ConfigurationCompiler.Fail(
                    ConfigurationError.AppendPointer(pointer, "when"),
                    "the edge carries a guard, and the compilation context binds no guard evaluator. Bind "
                    + "AgentCompilationContext.Guards, which runs the rule. AddAgentCore binds it to "
                    + "GuardEvaluator. A guarded edge that silently became unconditional is exactly "
                    + "the silent graph failure section 8.2 refuses to ship.");
            }
            
            // One gate per guarded edge, holding the run until its guard is true. The gate is a
            // workflow executor rather than an edge predicate because the predicate only ever sees
            // the edge message: per-call state reaches the gate through the run, filed by the
            // graph-state entry. object, not List<ChatMessage>: the guard reads state, so every
            // message on the edge takes the same answer and the turn token is not filtered out.
            var gate = new FunctionExecutor<object>(
                $"agentcore-gate-{index}",
                (message, gateContext, gateToken) => GraphGuardGate.RouteAsync(
                    message, gateContext, guard, evaluator, gateToken),
                null,
                [typeof(object)],
                null,
                true);

            builder = builder.AddEdge(from, gate);
            builder = builder.AddEdge(gate, to);
            guarded = true;
        }

        if (outputs.Count > 0)
        {
            builder = builder.WithOutputFrom([.. outputs]);
        }

        var compiled = builder.WithName(entryName)
                              .Build()
                              .AsAIAgent(name: entryName);

        AIAgent withOutputCheck = new RequireOutputAgent(compiled, entryName);

        return (
            guarded ? new GraphStateAgent(withOutputCheck, entryName) : withOutputCheck,
            NoStages());
    }

    /// <remarks>An explicit graph answers from its <c>output: true</c> nodes.</remarks>
    internal override HashSet<string>? SpokenAuthors(AgentCoreConfiguration configuration, EntryConfiguration entry)
    {
        HashSet<string> outputs = new(StringComparer.Ordinal);
        foreach (var node in entry.Graph!.Nodes)
        {
            if (node.Output && node.Agent is { } agentId)
            {
                outputs.Add(agentId);
            }
        }

        return outputs.Count == 0 ? null : outputs;
    }
}
