using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using AgentCore.Application.Runtime.Agents;
using AgentCore.Application.Runtime.Agents.Graph;

namespace AgentCore.Application.Configuration.Compilation
{
    /// <summary>
    /// Holds <c>graph:</c> with <c>nodes:</c> and <c>edges:</c>. It builds a
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
            AgentCompilationContext context,
            bool reusesGraphSession)
        {
            GraphConfiguration graph = entry.Graph!;
            string graphPointer = ConfigurationError.AppendPointer(entryPointer, "graph");

            (Dictionary<string, ExecutorBinding>? nodes, string? start, List<ExecutorBinding>? outputs) = BindNodes(
                configuration, entryName, graph, ConfigurationError.AppendPointer(graphPointer, "nodes"), agents);

            ExecutorBinding stateEntry = ExecutorBindingExtensions.BindExecutor(new GraphStateEntry());

            WorkflowBuilder builder = new(stateEntry);

            builder = builder.AddEdge(stateEntry, nodes[start]);
            (WorkflowBuilder? built, bool guarded) = AddEdges(builder, graph, ConfigurationError.AppendPointer(graphPointer, "edges"), nodes, context);

            if (outputs.Count > 0)
            {
                built = built.WithOutputFrom([.. outputs]);
            }

            AIAgent compiled = built
                .WithName(entryName)
                .Build()
                .AsAIAgent(name: entryName);

            AIAgent withOutputCheck = new RequireOutputAgent(new GraphFaultAgent(compiled, drain: reusesGraphSession), entryName);

            return new EntryBuild(
                guarded ? new GraphStateAgent(withOutputCheck, entryName) : withOutputCheck,
                NoStages());
        }

        /// <summary>Binds every node to its agent, and finds the one start node.</summary>
        private static (Dictionary<string, ExecutorBinding> Nodes, string Start, List<ExecutorBinding> Outputs) BindNodes(
            AgentCoreConfiguration configuration,
            string entryName,
            GraphConfiguration graph,
            string nodesPointer,
            Dictionary<string, AIAgent> agents)
        {
            Dictionary<string, ExecutorBinding> nodes = new(StringComparer.Ordinal);
            List<string> starts = [];
            List<ExecutorBinding> outputs = [];

            for (int index = 0; index < graph.Nodes.Count; index++)
            {
                GraphNodeConfiguration node = graph.Nodes[index];
                AIAgentBinding binding = BindNode(configuration, entryName, node, ConfigurationError.AppendPointer(nodesPointer, index), agents, nodes);
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

            return starts.Count != 1
                ? throw ConfigurationCompiler.Fail(
                    nodesPointer,
                    $"the graph declares {starts.Count} start nodes. Check 7 of section 8.5 needs exactly one.")
                : ((Dictionary<string, ExecutorBinding> Nodes, string Start, List<ExecutorBinding> Outputs))(nodes, starts[0], outputs);
        }

        private static AIAgentBinding BindNode(
            AgentCoreConfiguration configuration,
            string entryName,
            GraphNodeConfiguration node,
            string pointer,
            Dictionary<string, AIAgent> agents,
            Dictionary<string, ExecutorBinding> nodes)
        {
            if (node.Agent is not { } agentId || !agents.TryGetValue(agentId, out AIAgent? agent))
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

            if (ConfigurationCompiler.AgentDeclaresBackground(configuration, agentId))
            {
                throw ConfigurationCompiler.FailBackgroundInGraph(ConfigurationError.AppendPointer(pointer, "agent"), agentId, entryName);
            }

            return new AIAgentBinding(new GraphParticipantAgent(agent), new AIAgentHostOptions { EmitAgentUpdateEvents = true });
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
            bool guarded = false;

            for (int index = 0; index < graph.Edges.Count; index++)
            {
                GraphEdgeConfiguration edge = graph.Edges[index];
                string pointer = ConfigurationError.AppendPointer(edgesPointer, index);
                ExecutorBinding from = Endpoint(nodes, edge.From, pointer, "from");
                ExecutorBinding to = Endpoint(nodes, edge.To, pointer, "to");

                if (edge.When is not { } guard)
                {
                    builder = builder.AddEdge(from, to);
                    continue;
                }

                FunctionExecutor<object> gate = Gate(index, guard, Evaluator(context, pointer));
                builder = builder.AddEdge(from, gate);
                builder = builder.AddEdge(gate, to);
                guarded = true;
            }

            return (builder, guarded);
        }

        private static ExecutorBinding Endpoint(Dictionary<string, ExecutorBinding> nodes, string id, string pointer, string key)
        {
            return nodes.TryGetValue(id, out ExecutorBinding? node)
                        ? node
                        : throw ConfigurationCompiler.Fail(
                            ConfigurationError.AppendPointer(pointer, key),
                            $"the node '{id}' is not declared.");
        }

        private static IGuardEvaluator Evaluator(AgentCompilationContext context, string pointer)
        {
            return context.Guards ?? throw ConfigurationCompiler.Fail(
                        ConfigurationError.AppendPointer(pointer, "when"),
                        "the edge carries a guard, and the compilation context binds no guard evaluator. Bind "
                        + "AgentCompilationContext.Guards, which runs the rule. AddAgentCore binds it to "
                        + "GuardEvaluator. A guarded edge that silently became unconditional is exactly "
                        + "the silent graph failure section 8.2 refuses to ship.");
        }

        /// <summary>
        /// One gate per guarded edge, holding the run until its guard is true. The gate is a
        /// workflow executor rather than an edge predicate because the predicate only ever sees
        /// the edge message: per-call state reaches the gate through the run, filed by the
        /// graph-state entry. object, not List&lt;ChatMessage&gt;: the guard reads state, so every
        /// message on the edge takes the same answer and the turn token is not filtered out.
        /// </summary>
        private static FunctionExecutor<object> Gate(int index, GuardReference guard, IGuardEvaluator evaluator)
        {
            return new(
                        $"agentcore-gate-{index}",
                        (message, gateContext, gateToken) => GraphGuardGate.RouteAsync(
                            message, gateContext, guard, evaluator, gateToken),
                        null,
                        [typeof(object)],
                        null,
                        true);
        }

        internal override HashSet<string>? SpokenAuthors(AgentCoreConfiguration configuration, EntryConfiguration entry)
        {
            HashSet<string> outputs = new(StringComparer.Ordinal);
            foreach (GraphNodeConfiguration node in entry.Graph!.Nodes)
            {
                if (node.Output && node.Agent is { } agentId)
                {
                    _ = outputs.Add(agentId);
                }
            }

            return outputs.Count == 0 ? null : outputs;
        }
    }
}
