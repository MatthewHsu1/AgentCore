using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Evaluation;
using AgentCore.Application.Knowledge;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Transcript;
using Microsoft.Agents.AI;
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
///   <description><see cref="SingleAgentRow"/>: <c>ChatClientAgent</c>, with no runtime</description>
/// </item>
/// <item>
///   <term><c>agents:</c> plus <c>policy:</c></term>
///   <description><see cref="PolicyRow"/>: the machine picks a stage each turn, and the stage names one agent. Runtime is <c>Stateless</c></description>
/// </item>
/// <item>
///   <term><c>graph:</c> with <c>pattern:</c></term>
///   <description><see cref="PatternGraphRow"/>: one of the four <c>AgentWorkflowBuilder</c> shapes</description>
/// </item>
/// <item>
///   <term><c>graph:</c> with <c>nodes:</c> and <c>edges:</c></term>
///   <description><see cref="ExplicitGraphRow"/>: <c>WorkflowBuilder</c>, agents bound as executors, then <c>AsAIAgent()</c></description>
/// </item>
/// <item>
///   <term>both <c>policy:</c> and <c>graph:</c></term>
///   <description>a load-time error</description>
/// </item>
/// </list>
/// <para>
/// Each row is a <see cref="CompileTableRow"/>. This class selects the row and builds what every
/// row shares: the <c>agents.items</c> entries, their tools, and the turn-disposition layers.
/// </para>
/// <para>
/// Every failure here reports through <see cref="ConfigurationLoadException"/> and carries a JSON
/// Pointer, exactly as the eight checks of section 8.5 do.
/// </para>
/// </remarks>
public static class ConfigurationCompiler
{
    /// <summary>Picks the row of the compile table one document selects.</summary>
    /// <param name="configuration">The loaded document.</param>
    /// <returns>The row.</returns>
    /// <exception cref="ConfigurationLoadException">The document selects no row, or selects two.</exception>
    public static CompiledAgentShape SelectShape(AgentCoreConfiguration configuration)
        => SelectRow(configuration).Shape;

    internal static CompileTableRow SelectRow(AgentCoreConfiguration configuration)
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
                return PatternGraphRow.Instance;
            }

            if (hasNodes)
            {
                return ExplicitGraphRow.Instance;
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

            return PolicyRow.Instance;
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

        return SingleAgentRow.Instance;
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

        var row = SelectRow(configuration);

        AgentCoreChatHistoryProvider history = new(context.TranscriptStore);

        var agents = BuildAgents(configuration, context, row.SessionCarriesHistory ? history : null);

        var (entry, stages) = row.BuildEntry(configuration, agents, context);

        var spokenBy = row.SpokenAuthors(configuration);

        return new CompiledAgent(
            configuration,
            row,
            entry,
            agents,
            stages,
            spokenBy,
            history,
            inner => WithTurnDisposition(inner, configuration, context.Moderation, spokenBy));
    }

    /// <summary>Puts the two turn-disposition layers on one agent a turn runs.</summary>
    /// <param name="agent">The compiled agent of one row, or of one <c>policy:</c> stage.</param>
    /// <param name="configuration">The document. It names the fallback line and the refusal line.</param>
    /// <param name="moderation">The endpoint seam, or <see langword="null"/> to moderate nothing.</param>
    /// <param name="spokenBy">The agents whose reply the caller hears, or <see langword="null"/> for all.</param>
    /// <returns>The agent the turn loop runs.</returns>
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
    /// <param name="history">Store 1, or <see langword="null"/> to leave the framework default in place.</param>
    /// <returns>The agents, keyed by id.</returns>
    private static Dictionary<string, AIAgent> BuildAgents(
        AgentCoreConfiguration configuration,
        AgentCompilationContext context,
        AgentCoreChatHistoryProvider? history)
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
                new ChatClientAgentOptions
                {
                    Name = item.Id,
                    ChatOptions = new ChatOptions
                    {
                        Instructions = AgentInstructions.Compose(section.Defaults, item),
                        Tools = BuildTools(item, tools, context, pointer, Resolve),
                    },
                    ChatHistoryProvider = history,
                    AIContextProviders = BuildContextProviders(section.Defaults, item, context, pointer),
                });
            path.RemoveAt(path.Count - 1);

            var instrumented = new AIAgentBuilder(built)
                .UseOpenTelemetry(configure: static agent => agent.EnableSensitiveData = false)
                .Build();

            agents[id] = instrumented;
            return instrumented;
        }
    }

    /// <summary>Builds the context providers of one agent.</summary>
    /// <param name="defaults">The <c>agents.defaults</c> section, or <see langword="null"/>.</param>
    /// <param name="item">The agent being built.</param>
    /// <param name="context">The seams the host bound, one of which may be the knowledge store.</param>
    /// <param name="pointer">The JSON Pointer at this agent.</param>
    /// <returns>The providers, in the order the framework runs them.</returns>
    /// <exception cref="ConfigurationLoadException">
    /// The agent declares a <c>knowledge:</c> block and the host bound no knowledge store.
    /// </exception>
    private static List<AIContextProvider> BuildContextProviders(
        AgentDefaults? defaults,
        AgentConfiguration item,
        AgentCompilationContext context,
        string pointer)
    {
        List<AIContextProvider> providers = [new TurnContextProvider()];

        if (AgentKnowledge.Compose(defaults, item) is not { } knowledge)
        {
            return providers;
        }

        if (context.Knowledge is not { } port)
        {
            throw Fail(
                ConfigurationError.AppendPointer(pointer, "knowledge"),
                $"the agent '{item.Id}' declares a knowledge: block and this host registered no "
                + "knowledge vendor, so there is no store to read. Call "
                + "options.UseKnowledgeStores(...) with an adapter that serves "
                + $"{nameof(IKnowledgeRetrievalPort)}, or remove the knowledge: block.");
        }

        providers.Add(KnowledgeProviderFactory.Create(
            port,
            knowledge,
            item.Id,
            context.Citations ?? new SourceLocatorCitationFormatter(),
            context.Loggers));

        return providers;
    }

    /// <summary>Puts the auditing function-invocation loop into the pipeline of one agent.</summary>
    private static AuditingFunctionInvokingChatClient WithToolFailureAuditing(IChatClient model)
        => new(model.AsBuilder()
                    .UseOpenTelemetry(configure: static client => client.EnableSensitiveData = false)
                    .Use(static innerClient => new ModelFacingChatClient(innerClient))
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
                if (context.Tools is { } discovered && discovered.Contains(id))
                {
                    tools.Add(discovered.Resolve(id));
                    continue;
                }

                if (context.Tools is null)
                {
                    // No factory, so nothing this loop could have built anyway.
                    continue;
                }

                throw Fail(toolPointer, $"the tool id '{id}' is not declared in tools:, and no tool source serves it.");
            }

            if (tool.Kind == ToolKind.Agent)
            {
                // A kind: agent tool needs no tool factory. Section 7 says section 8 adds no port,
                // and this kind adds none either: the inner agent is already in the document.
                tools.Add(AgentDelegationTool.Create(tool, ResolveInner(tool, resolveAgent, toolPointer)));
                continue;
            }

            if (context.Tools is { } registry)
            {
                if (!registry.Contains(id))
                {
                    throw Fail(toolPointer, $"the tool id '{id}' is declared, and no tool source serves it.");
                }

                tools.Add(registry.Resolve(id));
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

    internal static ConfigurationLoadException Fail(string pointer, string message)
        => new(new ConfigurationError
        {
            Pointer = pointer,
            Message = message,

            // The compile table is a shape rule over the whole document, like check 1.
            Check = ConfigurationCheck.DocumentSchema,
        });
}
