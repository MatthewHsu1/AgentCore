using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Evaluation;
using AgentCore.Application.Runtime;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Configuration.Compilation;

/// <summary>
/// The compile table of section 8.2. It is a table, not a heuristic.
/// </summary>
/// <remarks>
/// <list type="table">
/// <listheader><term>The document holds</term><description>AgentCore builds</description></listheader>
/// <item>
///   <term>one <c>agents.items</c> entry, no <c>policy:</c>, no <c>graph:</c></term>
///   <description><c>ChatClientAgent</c>, with no runtime</description>
/// </item>
/// <item>
///   <term><c>agents:</c> plus <c>policy:</c></term>
///   <description>the machine picks a stage each turn, and the stage names one agent. Runtime is <c>Stateless</c></description>
/// </item>
/// <item>
///   <term><c>graph:</c> with <c>pattern:</c></term>
///   <description><c>AgentWorkflowBuilder.BuildSequential</c>, <c>BuildConcurrent</c>, <c>CreateHandoffBuilderWith</c>, or <c>CreateGroupChatBuilderWith</c></description>
/// </item>
/// <item>
///   <term><c>graph:</c> with <c>nodes:</c> and <c>edges:</c></term>
///   <description><c>WorkflowBuilder</c>, agents bound as executors, then <c>AsAIAgent()</c></description>
/// </item>
/// <item>
///   <term>both <c>policy:</c> and <c>graph:</c></term>
///   <description>a load-time error</description>
/// </item>
/// </list>
/// <para>
/// Every failure here reports through <see cref="ConfigurationLoadException"/> and carries a JSON
/// Pointer, exactly as the eight checks of section 8.5 do.
/// </para>
/// </remarks>
public static class ConfigurationCompiler
{
    /// <summary>The reason a row 4 graph that produced no text reports, after it names the graph.</summary>
    /// <remarks>
    /// It is a constant so a test asserts the message a host reads, rather than a copy of it. It stays
    /// internal, because a host reads this message and never matches it. D15 makes every public member
    /// a compatibility obligation, and the value of a public constant is one as well.
    /// </remarks>
    internal const string NoOutputMessage =
        "Row 4 of the section 8.2 compile table ends the run at idle when no outgoing edge guard of "
        + "the node that holds the run is true. Measured on Microsoft.Agents.AI.Workflows 1.17.0: "
        + "InProcessRunner raises no exception for that, and the AsAIAgent() wrapper emits no update, "
        + "so the caller would read an empty run and no error. Read the when: guard of every edge that "
        + "leaves the node the run reached, and make one of them true for this state. A stage says "
        + "onNoMatch: error, and a graph node takes no such key: D15 makes a new public key a "
        + "permanent obligation, so this check adds none.";

    private const string NoAgentId = "";

    /// <summary>Picks the row of the compile table one document selects.</summary>
    /// <param name="configuration">The loaded document.</param>
    /// <returns>The row.</returns>
    /// <exception cref="ConfigurationLoadException">The document selects no row, or selects two.</exception>
    public static CompiledAgentShape SelectShape(AgentCoreConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // Row 5. A document holds policy: or graph:, never both.
        if (configuration.Policy is not null && configuration.Graph is not null)
        {
            throw Fail(
                ConfigurationError.RootPointer,
                "the document holds both policy: and graph:. Row 5 of the section 8.2 compile table "
                + "rejects that: a stage machine and a workflow graph are two runtimes, and one document "
                + "selects one row. Keep policy: for a call that walks stages, and graph: for a run that "
                + "needs checkpointing, a request port, or a parallel fan-out with a join.");
        }

        if (configuration.Graph is { } graph)
        {
            // Row 3 and row 4.
            var hasPattern = graph.Pattern is not null;
            var hasNodes = graph.Nodes.Count > 0 || graph.Edges.Count > 0;

            if (hasPattern && hasNodes)
            {
                throw Fail("/graph", "the graph holds both pattern: and nodes:. It holds one or the other.");
            }

            if (hasPattern)
            {
                return CompiledAgentShape.PatternGraph;
            }

            if (hasNodes)
            {
                return CompiledAgentShape.ExplicitGraph;
            }

            throw Fail("/graph", "the graph declares neither pattern: nor nodes: and edges:.");
        }

        if (configuration.Policy is not null)
        {
            // Row 2.
            if (configuration.Agents is null || configuration.Agents.Items.Count == 0)
            {
                throw Fail("/policy", "the document declares policy: and no agents:. Each stage names one agent.");
            }

            return CompiledAgentShape.Policy;
        }

        // Row 1.
        if (configuration.Agents is not { } agents || agents.Items.Count == 0)
        {
            throw Fail(
                ConfigurationError.RootPointer,
                "the document declares no agents:, no policy:, and no graph:, so it compiles to nothing.");
        }

        if (agents.Items.Count > 1)
        {
            throw Fail(
                "/agents/items",
                $"the document declares {agents.Items.Count} agents and neither policy: nor graph:, so "
                + "nothing picks between them. Row 1 of the section 8.2 compile table takes exactly one agent.");
        }

        return CompiledAgentShape.SingleAgent;
    }

    /// <summary>Compiles one document through the row it selects.</summary>
    /// <param name="configuration">The loaded document.</param>
    /// <param name="context">The seams the document names.</param>
    /// <returns>The compiled agent. It is a process singleton: see T44.</returns>
    /// <exception cref="ConfigurationLoadException">The document does not compile.</exception>
    public static CompiledAgent Compile(AgentCoreConfiguration configuration, AgentCompilationContext context)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(context);

        var shape = SelectShape(configuration);
        var agents = BuildAgents(configuration, context);

        var (entry, stages) = shape switch
        {
            CompiledAgentShape.SingleAgent => (agents[configuration.Agents!.Items[0].Id], NoStages()),
            CompiledAgentShape.Policy => BuildPolicyEntry(configuration, agents),
            CompiledAgentShape.PatternGraph => (BuildPatternGraph(configuration, agents), NoStages()),
            _ => (BuildExplicitGraph(configuration, agents, context), NoStages()),
        };

        // R1, R2 and R3 are rules about a TURN, and one turn is one run of one agent on every row of
        // the compile table. The layers therefore go on the agent a turn runs and never on a graph
        // node, which is one step inside a turn. CompiledAgent.Agent stays the bare compiled
        // artifact, so a host that runs it itself still reads the fault section 8.2 asks it to raise.
        var spokenBy = SpokenAuthors(configuration, shape);

        return new CompiledAgent(
            configuration,
            shape,
            entry,
            agents,
            stages,
            spokenBy,
            inner => WithTurnDisposition(inner, configuration, context.Moderation, spokenBy));
    }

    /// <summary>Names the agents whose reply the caller actually hears.</summary>
    /// <param name="configuration">The loaded document.</param>
    /// <param name="shape">The row this document compiles through.</param>
    /// <returns>
    /// The <c>agents.items</c> ids that answer the caller, or <see langword="null"/> when the last
    /// thing the run produced is the answer.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Rows 1 and 2 run one agent, so the run's own last message is the reply and no filter is
    /// needed. A sequential graph answers from its last participant, and an explicit graph from its
    /// <c>output: true</c> nodes.
    /// </para>
    /// <para>
    /// The other three patterns name no answering agent. Concurrent aggregates every participant,
    /// and handoff and group chat both end wherever the conversation took them, so on those rows any
    /// participant may legitimately speak last.
    /// </para>
    /// </remarks>
    private static HashSet<string>? SpokenAuthors(AgentCoreConfiguration configuration, CompiledAgentShape shape)
    {
        if (configuration.Graph is not { } graph)
        {
            return null;
        }

        if (shape is CompiledAgentShape.PatternGraph)
        {
            return graph is { Pattern: GraphPattern.Sequential, Agents.Count: > 0 }
                ? new HashSet<string>(StringComparer.Ordinal) { graph.Agents[^1] }
                : null;
        }

        HashSet<string> outputs = new(StringComparer.Ordinal);
        foreach (var node in graph.Nodes)
        {
            if (node.Output && node.Agent is { } agentId)
            {
                outputs.Add(agentId);
            }
        }

        return outputs.Count == 0 ? null : outputs;
    }

    /// <summary>Puts the two turn-disposition layers on one agent a turn runs.</summary>
    /// <param name="agent">The compiled agent of one row, or of one <c>policy:</c> stage.</param>
    /// <param name="configuration">The document. It names the fallback line and the refusal line.</param>
    /// <param name="moderation">The endpoint seam, or <see langword="null"/> to moderate nothing.</param>
    /// <param name="spokenBy">The agents whose reply the caller hears, or <see langword="null"/> for all.</param>
    /// <returns>The agent the turn loop runs.</returns>
    /// <remarks>
    /// Moderation goes outside the fallback, so the verdict survives a run that then threw: the
    /// fallback layer marks the run it answered, and moderation folds that mark into its own.
    /// </remarks>
    private static AIAgent WithTurnDisposition(
        AIAgent agent,
        AgentCoreConfiguration configuration,
        PromptModerator? moderation,
        IReadOnlySet<string>? spokenBy)
    {
        AIAgent layered = new FallbackAgent(agent, configuration.FallbackReply, spokenBy);

        return moderation is null
            ? layered
            : new ModerationAgent(layered, moderation, configuration.RefusalReply, ModerationAgent.DefaultTimeout);
    }

    /// <summary>Builds one <c>ChatClientAgent</c> for each <c>agents.items</c> entry.</summary>
    /// <param name="configuration">The loaded document.</param>
    /// <param name="context">The seams the document names.</param>
    /// <returns>The agents, keyed by id.</returns>
    private static Dictionary<string, AIAgent> BuildAgents(
        AgentCoreConfiguration configuration,
        AgentCompilationContext context)
    {
        Dictionary<string, AIAgent> agents = new(StringComparer.Ordinal);
        if (configuration.Agents is not { } section)
        {
            return agents;
        }

        Dictionary<string, ToolConfiguration> tools = new(StringComparer.Ordinal);
        foreach (var tool in configuration.Tools)
        {
            tools[tool.Id] = tool;
        }

        Dictionary<string, int> declaredAt = new(StringComparer.Ordinal);
        for (var index = 0; index < section.Items.Count; index++)
        {
            if (!declaredAt.TryAdd(section.Items[index].Id, index))
            {
                throw Fail(
                    ConfigurationError.AppendPointer(ConfigurationError.AppendPointer("/agents/items", index), "id"),
                    $"the agent id '{section.Items[index].Id}' is declared twice.");
            }
        }

        // A kind: agent tool names another agents.items entry, so one agent may need a second one
        // that this walk has not reached yet. The walk therefore resolves on demand instead of in
        // declaration order, and it holds the agents it entered so a delegation loop becomes a
        // compile error rather than a stack overflow.
        List<string> path = [];

        foreach (var item in section.Items)
        {
            Resolve(item.Id);
        }

        return agents;

        AIAgent? Resolve(string id)
        {
            if (agents.TryGetValue(id, out var existing))
            {
                // Built once, then shared. A delegating agent reuses the inner agent and never
                // compiles a second copy of it. See T44 and CompiledAgentRegistry.
                return existing;
            }

            if (!declaredAt.TryGetValue(id, out var index))
            {
                return null;
            }

            var item = section.Items[index];
            var pointer = ConfigurationError.AppendPointer("/agents/items", index);

            if (path.Contains(id, StringComparer.Ordinal))
            {
                throw Fail(
                    pointer,
                    $"the agent '{id}' delegates back to itself through a kind: agent tool: "
                    + $"{string.Join(" -> ", path)} -> {id}. Check 8 of section 8.5 rejects a delegation "
                    + "cycle, because the call would never return.");
            }

            path.Add(id);
            var built = new ChatClientAgent(
                WithToolFailureAuditing(context.ChatClients.GetChatClient(item.Model ?? section.Defaults?.Model)),
                AgentInstructions.Compose(section.Defaults, item),
                item.Id,
                description: null,
                BuildTools(item, tools, context, pointer, Resolve));
            path.RemoveAt(path.Count - 1);

            // Every agent this compiler ever builds is built here and nowhere else — the dictionary
            // above is the only cache, row 2's stage lookup and rows 3/4's graphs all read the same
            // built instance back out of it. Wrapping here, once, before the instance is cached, is
            // therefore what makes "instrumented exactly once" true: a second Resolve of the same id
            // returns the already-wrapped agent at the top of this function and never reaches this line.
            var instrumented = new AIAgentBuilder(built)
                .UseOpenTelemetry(configure: static agent => agent.EnableSensitiveData = false)
                .Build();

            agents[id] = instrumented;
            return instrumented;
        }
    }

    /// <summary>Puts the auditing function-invocation loop into the pipeline of one agent.</summary>
    /// <param name="model">The client <c>providers.llm</c> resolved for this agent.</param>
    /// <returns>The client the agent sends on.</returns>
    /// <remarks>
    /// <para>
    /// This is how the observer gets into the pipeline, and it works because of one line in
    /// <c>Microsoft.Agents.AI</c>: <c>ChatClientExtensions.WithDefaultAgentMiddleware</c> tests
    /// <c>chatClient.GetService&lt;FunctionInvokingChatClient&gt;()</c> FIRST and adds one of its own
    /// only when it finds none. A <c>ChatClientAgent</c> handed a client that already invokes
    /// functions therefore uses that one, and there is exactly one loop in the pipeline either way.
    /// Verified against Microsoft.Agents.AI 1.17.0.
    /// </para>
    /// <para>
    /// It wraps here, and not in <c>CompositeChatClientFactory</c>, because this is the seam where an
    /// AGENT is built. The factory owns one vendor client for each <c>providers.llm[].as</c> name and
    /// disposes it, and it also answers the extractor, which is a plain completion call with no tools
    /// and nothing to observe. This wrapper is stateless, holds nothing per call, and is never
    /// disposed, so the shared vendor client below it keeps the single owner it had.
    /// </para>
    /// <para>
    /// <b>Chat telemetry is wired here, and not left to MAF's own slot, for a reason worth recording.</b>
    /// MAF 1.17.0 reserves a slot for chat telemetry — <c>internal sealed class
    /// DeferredOpenTelemetryChatClient</c>, added by <c>ChatClientExtensions.WithDefaultAgentMiddleware</c>
    /// — that <c>OpenTelemetryAgent</c> activates once the built <c>ChatClientAgent</c> is wrapped for
    /// OpenTelemetry. That slot is always inserted as the LAST call in a builder whose factories run in
    /// reverse order, so it lands directly below whichever <c>FunctionInvokingChatClient</c> the SAME
    /// builder call finds already present on the client it was handed — and only below a loop MAF
    /// inserts itself, at the position it inserts it, does the slot end up correctly positioned. This
    /// loop is not MAF's own: it is supplied from underneath, as the <c>chatClient</c> argument
    /// <c>WithDefaultAgentMiddleware</c> receives, so its own <c>GetService&lt;FunctionInvokingChatClient&gt;</c>
    /// check finds THIS loop already present and skips inserting one of its own — and the slot MAF still
    /// adds lands ABOVE this loop, wrapping the whole tool-calling round trip as one span instead of one
    /// span per model call. Because the slot is <c>internal sealed</c>, it cannot be constructed and
    /// placed by hand in the right spot either. The fix is therefore to skip the slot entirely: wrap
    /// <paramref name="model"/> in the library's own, always-active
    /// <see cref="Microsoft.Extensions.AI.OpenTelemetryChatClient"/> — verified public in Microsoft.Extensions.AI
    /// 10.8.3 — directly, here, BELOW this loop, before <see cref="AuditingFunctionInvokingChatClient"/>
    /// is constructed around it. <see cref="Microsoft.Extensions.AI.FunctionInvokingChatClient"/> reads
    /// <c>Activity.Current</c> at the moment it invokes a tool, and with a real chat client in place the
    /// per-round chat span opens and closes before that moment, so <c>Activity.Current</c> has reverted
    /// to the <c>invoke_agent</c> span the agent wrapper opened and <c>execute_tool</c> nests under it
    /// rather than under one span covering the whole loop. MAF's own deferred slot is still inserted
    /// above this pipeline by <c>WithDefaultAgentMiddleware</c>, but <c>OpenTelemetryAgent</c> looks for
    /// an <c>OpenTelemetryChatClient</c> before it looks for that slot, finds the one wired in here, and
    /// never activates the slot — so exactly one chat-level instrumentation layer is ever live.
    /// </para>
    /// </remarks>
    private static AuditingFunctionInvokingChatClient WithToolFailureAuditing(IChatClient model)
        => new(
            model.AsBuilder()
                .UseOpenTelemetry(configure: static client => client.EnableSensitiveData = false)
                .Build());

    private static List<AITool>? BuildTools(
        AgentConfiguration item,
        Dictionary<string, ToolConfiguration> declared,
        AgentCompilationContext context,
        string pointer,
        Func<string, AIAgent?> resolveAgent)
    {
        if (item.Tools.Count == 0)
        {
            return null;
        }

        List<AITool> tools = [];
        for (var index = 0; index < item.Tools.Count; index++)
        {
            var id = item.Tools[index];
            var toolPointer = ConfigurationError.AppendPointer(
                ConfigurationError.AppendPointer(pointer, "tools"), index);

            if (!declared.TryGetValue(id, out var tool))
            {
                if (context.Tools is null)
                {
                    // No factory, so nothing this loop could have built anyway.
                    continue;
                }

                throw Fail(toolPointer, $"the tool id '{id}' is not declared in tools:.");
            }

            if (tool.Kind == ToolKind.Agent)
            {
                // A kind: agent tool needs no tool factory. Section 7 says section 8 adds no port,
                // and this kind adds none either: the inner agent is already in the document.
                tools.Add(AgentDelegationTool.Create(tool, ResolveInner(tool, resolveAgent, toolPointer)));
                continue;
            }

            if (context.Tools?.Create(tool) is { } built)
            {
                tools.Add(built);
            }
        }

        return tools.Count == 0 ? null : tools;
    }

    private static AIAgent ResolveInner(ToolConfiguration tool, Func<string, AIAgent?> resolveAgent, string pointer)
    {
        if (tool.Agent is not { Length: > 0 } id)
        {
            throw Fail(pointer, $"the tool '{tool.Id}' is kind: agent and names no agent:.");
        }

        return resolveAgent(id)
            ?? throw Fail(
                pointer,
                $"the tool '{tool.Id}' delegates to the agent '{id}', which agents.items does not declare.");
    }

    private static (AIAgent Entry, Dictionary<string, string> Stages) BuildPolicyEntry(
        AgentCoreConfiguration configuration,
        Dictionary<string, AIAgent> agents)
    {
        var policy = configuration.Policy!;
        Dictionary<string, string> stages = new(StringComparer.Ordinal);

        for (var index = 0; index < policy.Stages.Count; index++)
        {
            var stage = policy.Stages[index];
            if (stage.Agent is not { } agentId)
            {
                stages[stage.Id] = NoAgentId;
                continue;
            }

            if (!agents.ContainsKey(agentId))
            {
                throw Fail(
                    ConfigurationError.AppendPointer(
                        ConfigurationError.AppendPointer("/policy/stages", index), "agent"),
                    $"the stage '{stage.Id}' names the agent '{agentId}', which agents.items does not declare.");
            }

            stages[stage.Id] = agentId;
        }

        if (!stages.TryGetValue(policy.Initial, out var initialAgent))
        {
            throw Fail("/policy/initial", $"the initial stage '{policy.Initial}' is not declared in policy.stages.");
        }

        if (initialAgent.Length == 0)
        {
            throw Fail("/policy/initial", $"the initial stage '{policy.Initial}' names no agent, so no turn can run.");
        }

        return (agents[initialAgent], stages);
    }

    private static AIAgent BuildPatternGraph(
        AgentCoreConfiguration configuration,
        Dictionary<string, AIAgent> agents)
    {
        var graph = configuration.Graph!;
        List<AIAgent> participants = [];

        for (var index = 0; index < graph.Agents.Count; index++)
        {
            var id = graph.Agents[index];
            if (!agents.TryGetValue(id, out var agent))
            {
                throw Fail(
                    ConfigurationError.AppendPointer("/graph/agents", index),
                    $"the graph names the agent '{id}', which agents.items does not declare.");
            }

            participants.Add(agent);
        }

        if (participants.Count == 0)
        {
            throw Fail("/graph/agents", "a pattern graph names no agent.");
        }

        var workflow = graph.Pattern switch
        {
            GraphPattern.Sequential => AgentWorkflowBuilder.BuildSequential(configuration.Name, participants),
            GraphPattern.Concurrent => AgentWorkflowBuilder.BuildConcurrent(configuration.Name, participants, aggregator: null),
            GraphPattern.Handoff => BuildHandoff(configuration.Name, participants),
            _ => BuildGroupChat(configuration.Name, participants),
        };

        return workflow.AsAIAgent(name: configuration.Name);
    }

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

    private static AIAgent BuildExplicitGraph(
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
                throw Fail(
                    ConfigurationError.AppendPointer(pointer, "agent"),
                    $"the node '{node.Id}' names no declared agent, so nothing binds as its executor.");
            }

            if (nodes.ContainsKey(node.Id))
            {
                throw Fail(ConfigurationError.AppendPointer(pointer, "id"), $"the node id '{node.Id}' is declared twice.");
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
            throw Fail(
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
                throw Fail(ConfigurationError.AppendPointer(pointer, "from"), $"the node '{edge.From}' is not declared.");
            }

            if (!nodes.TryGetValue(edge.To, out var to))
            {
                throw Fail(ConfigurationError.AppendPointer(pointer, "to"), $"the node '{edge.To}' is not declared.");
            }

            if (edge.When is not { } guard)
            {
                builder = builder.AddEdge(from, to);
                continue;
            }

            if (context.Guards is not { } evaluator || context.StateSnapshot is not { } snapshot)
            {
                throw Fail(
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

        var compiled = builder.WithName(configuration.Name).Build().AsAIAgent(name: configuration.Name);

        // Every explicit graph takes the output check, guarded edges or not. The state pre-check goes
        // outside it, so a run with no state behind it still fails before the graph starts.
        var loud = RequireOutput(compiled, configuration.Name);
        return guardedState is null ? loud : RequireState(loud, guardedState);
    }

    /// <summary>Wraps an explicit graph so a run that produced no text fails loudly.</summary>
    /// <param name="graph">The workflow of row 4, already wrapped by <c>AsAIAgent()</c>.</param>
    /// <param name="name">The name of the document, which the failure reports.</param>
    /// <returns>The agent a caller runs.</returns>
    /// <remarks>
    /// <para>
    /// Each edge of row 4 may carry a <c>when:</c> guard. When every outgoing guard of the node that
    /// holds the run is false, the workflow ends at idle. Measured on
    /// <c>Microsoft.Agents.AI.Workflows</c> 1.17.0: <c>InProcessRunner</c> raises no exception for
    /// that, and the <c>AsAIAgent()</c> wrapper emits no update, so the caller reads an empty run and
    /// no error. That is the silent graph failure section 8.2 refuses to ship.
    /// </para>
    /// <para>
    /// A stage answers the same case with <c>onNoMatch: error</c>, and a graph node carries no such
    /// key. This check adds none either: D15 makes a new public key a permanent obligation, and the
    /// rule needs no document to state it, because a graph that answers nothing is never what a
    /// document meant.
    /// </para>
    /// <para>
    /// The check reads the text the run produced, because that is the one thing both paths share and
    /// the one thing a host consumes. Every node binding emits agent update events, so a node that
    /// spoke carries its own text out of the run and the check stays quiet.
    /// </para>
    /// </remarks>
    private static AIAgent RequireOutput(AIAgent graph, string name)
        => new AIAgentBuilder(graph)
            .Use(
                async (messages, session, options, inner, cancellationToken) =>
                {
                    var response = await inner.RunAsync(messages, session, options, cancellationToken)
                        .ConfigureAwait(false);

                    return string.IsNullOrWhiteSpace(response.Text) ? throw NoOutput(name) : response;
                },
                (messages, session, options, inner, cancellationToken)
                    => RequireOutputWhileStreaming(inner, messages, session, options, name, cancellationToken))
            .Build();

    /// <summary>Passes every update of one streaming run through, and fails after the last empty one.</summary>
    /// <param name="inner">The graph this wrapper sits in front of.</param>
    /// <param name="messages">What the caller asked.</param>
    /// <param name="session">The session the caller runs in, or <see langword="null"/>.</param>
    /// <param name="options">The options the caller passed, or <see langword="null"/>.</param>
    /// <param name="name">The name of the document, which the failure reports.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The updates the graph produced, unchanged and in order.</returns>
    /// <remarks>
    /// The streaming path holds nothing back. Rule 13 asks for the first delta before the model
    /// finishes, so each update leaves as it arrives and the check reads it on the way past. Only
    /// after the last update, when nothing carried text, does the run fail. This is an iterator, so
    /// the throw lands inside the enumeration and not on the call that built it, which is where
    /// <see cref="Runtime.CallSession.RunTurnStreamingAsync"/> catches it.
    /// </remarks>
    private static async IAsyncEnumerable<AgentResponseUpdate> RequireOutputWhileStreaming(
        AIAgent inner,
        IEnumerable<ChatMessage> messages,
        AgentSession? session,
        AgentRunOptions? options,
        string name,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var carriedText = false;

        await foreach (var update in inner.RunStreamingAsync(messages, session, options, cancellationToken)
            .ConfigureAwait(false))
        {
            carriedText |= !string.IsNullOrWhiteSpace(update.Text);
            yield return update;
        }

        if (!carriedText)
        {
            throw NoOutput(name);
        }
    }

    /// <summary>Builds the failure a row 4 graph that produced no text reports.</summary>
    /// <param name="name">The name of the document.</param>
    /// <returns>The fault the caller reads.</returns>
    private static InvalidOperationException NoOutput(string name)
        => new($"The graph '{name}' produced no text. " + NoOutputMessage);

    /// <summary>Wraps a guarded graph so a run with no state behind it fails before the graph starts.</summary>
    /// <param name="graph">The workflow of row 4, already wrapped by <c>AsAIAgent()</c>.</param>
    /// <param name="state">The state source the guarded edges read.</param>
    /// <returns>The agent a caller runs.</returns>
    /// <remarks>
    /// <para>
    /// A predicate that throws is not loud enough by itself. Measured on
    /// <c>Microsoft.Agents.AI.Workflows</c> 1.17.0: <c>InProcessRunner</c> catches every superstep
    /// fault and raises a <c>WorkflowErrorEvent</c> instead of throwing, and the <c>AsAIAgent()</c>
    /// wrapper turns no such event into an update. A guarded edge whose state source failed would
    /// therefore end the run at idle with no output and no error, which is the silent graph failure
    /// section 8.2 refuses to ship.
    /// </para>
    /// <para>
    /// The check runs once, before the graph does, where a throw still reaches the caller. It asks
    /// the same source the edges ask, so it needs no second seam and it knows nothing about how the
    /// host finds the state.
    /// </para>
    /// </remarks>
    private static AIAgent RequireState(AIAgent graph, Func<IReadOnlyDictionary<string, JsonNode?>> state)
        => new AIAgentBuilder(graph)
            .Use(
                (messages, session, options, inner, cancellationToken) =>
                {
                    _ = state();
                    return inner.RunAsync(messages, session, options, cancellationToken);
                },
                (messages, session, options, inner, cancellationToken) =>
                {
                    _ = state();
                    return inner.RunStreamingAsync(messages, session, options, cancellationToken);
                })
            .Build();

    /// <summary>Names the seams a guarded edge needs and the context left unbound.</summary>
    /// <param name="context">The seams the document names.</param>
    /// <returns>The phrase the failure message carries.</returns>
    private static string Missing(AgentCompilationContext context) => (context.Guards, context.StateSnapshot) switch
    {
        (null, null) => "no guard evaluator and no state source",
        (null, _) => "no guard evaluator",
        _ => "no state source",
    };

    private static Dictionary<string, string> NoStages() => new(StringComparer.Ordinal);

    private static ConfigurationLoadException Fail(string pointer, string message)
        => new(new ConfigurationError
        {
            Pointer = pointer,
            Message = message,

            // The compile table is a shape rule over the whole document, like check 1.
            Check = ConfigurationCheck.DocumentSchema,
        });
}
