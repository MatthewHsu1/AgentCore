using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
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

    internal override EntryBuild BuildEntry(
        AgentCoreConfiguration configuration,
        string entryName,
        EntryConfiguration entry,
        string entryPointer,
        Dictionary<string, AIAgent> agents,
        AgentCompilationContext context)
    {
        var graph = entry.Graph!;
        var graphPointer = ConfigurationError.AppendPointer(entryPointer, "graph");
        var (nodes, start, outputs) = BindNodes(graph, ConfigurationError.AppendPointer(graphPointer, "nodes"), agents);

        var stateEntry = ExecutorBindingExtensions.BindExecutor(new GraphStateEntry());

        WorkflowBuilder builder = new(stateEntry);
        builder = builder.AddEdge(stateEntry, nodes[start]);
        var (built, guarded) = AddEdges(builder, graph, ConfigurationError.AppendPointer(graphPointer, "edges"), nodes, context);

        if (outputs.Count > 0)
        {
            built = built.WithOutputFrom([.. outputs]);
        }

        var compiled = built.WithName(entryName)
                            .Build()
                            .AsAIAgent(name: entryName);

        AIAgent withOutputCheck = new RequireOutputAgent(compiled, entryName);

        return new EntryBuild(
            guarded ? new GraphStateAgent(withOutputCheck, entryName) : withOutputCheck,
            NoStages());
    }

    /// <summary>Binds every node to its agent, and finds the one start node.</summary>
    private static (Dictionary<string, ExecutorBinding> Nodes, string Start, List<ExecutorBinding> Outputs) BindNodes(
        GraphConfiguration graph,
        string nodesPointer,
        Dictionary<string, AIAgent> agents)
    {
        Dictionary<string, ExecutorBinding> nodes = new(StringComparer.Ordinal);
        List<string> starts = [];
        List<ExecutorBinding> outputs = [];

        for (var index = 0; index < graph.Nodes.Count; index++)
        {
            var node = graph.Nodes[index];
            var binding = BindNode(node, ConfigurationError.AppendPointer(nodesPointer, index), agents, nodes);
            nodes[node.Id] = binding;

            if (node.Start)
            {
                starts.Add(node.Id);
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

        return (nodes, starts[0], outputs);
    }

    private static AIAgentBinding BindNode(
        GraphNodeConfiguration node,
        string pointer,
        Dictionary<string, AIAgent> agents,
        Dictionary<string, ExecutorBinding> nodes)
    {
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
        return new AIAgentBinding(agent, new AIAgentHostOptions { EmitAgentUpdateEvents = true });
    }

    /// <summary>Adds every edge, with a gate in front of each guarded one.</summary>
    /// <returns>The builder, and whether any edge carries a guard.</returns>
    private static (WorkflowBuilder Builder, bool Guarded) AddEdges(
        WorkflowBuilder builder,
        GraphConfiguration graph,
        string edgesPointer,
        Dictionary<string, ExecutorBinding> nodes,
        AgentCompilationContext context)
    {
        var guarded = false;

        for (var index = 0; index < graph.Edges.Count; index++)
        {
            var edge = graph.Edges[index];
            var pointer = ConfigurationError.AppendPointer(edgesPointer, index);
            var from = Endpoint(nodes, edge.From, pointer, "from");
            var to = Endpoint(nodes, edge.To, pointer, "to");

            if (edge.When is not { } guard)
            {
                builder = builder.AddEdge(from, to);
                continue;
            }

            var gate = Gate(index, guard, Evaluator(context, pointer));
            builder = builder.AddEdge(from, gate);
            builder = builder.AddEdge(gate, to);
            guarded = true;
        }

        return (builder, guarded);
    }

    private static ExecutorBinding Endpoint(Dictionary<string, ExecutorBinding> nodes, string id, string pointer, string key)
        => nodes.TryGetValue(id, out var node)
            ? node
            : throw ConfigurationCompiler.Fail(
                ConfigurationError.AppendPointer(pointer, key),
                $"the node '{id}' is not declared.");

    private static IGuardEvaluator Evaluator(AgentCompilationContext context, string pointer)
        => context.Guards ?? throw ConfigurationCompiler.Fail(
            ConfigurationError.AppendPointer(pointer, "when"),
            "the edge carries a guard, and the compilation context binds no guard evaluator. Bind "
            + "AgentCompilationContext.Guards, which runs the rule. AddAgentCore binds it to "
            + "GuardEvaluator. A guarded edge that silently became unconditional is exactly "
            + "the silent graph failure section 8.2 refuses to ship.");

    /// <summary>
    /// One gate per guarded edge, holding the run until its guard is true. The gate is a
    /// workflow executor rather than an edge predicate because the predicate only ever sees
    /// the edge message: per-call state reaches the gate through the run, filed by the
    /// graph-state entry. object, not List&lt;ChatMessage&gt;: the guard reads state, so every
    /// message on the edge takes the same answer and the turn token is not filtered out.
    /// </summary>
    private static FunctionExecutor<object> Gate(int index, GuardReference guard, IGuardEvaluator evaluator)
        => new(
            $"agentcore-gate-{index}",
            (message, gateContext, gateToken) => GraphGuardGate.RouteAsync(
                message, gateContext, guard, evaluator, gateToken),
            null,
            [typeof(object)],
            null,
            true);

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
