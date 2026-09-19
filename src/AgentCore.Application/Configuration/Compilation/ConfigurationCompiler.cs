using AgentCore.Application.Conversation.Memory;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
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
/// <listheader><term>The entry holds</term><description>AgentCore builds</description></listheader>
/// <item>
///   <term><c>agent:</c></term>
///   <description><see cref="SingleAgentRow"/>: <c>ChatClientAgent</c>, with no runtime</description>
/// </item>
/// <item>
///   <term><c>policy:</c></term>
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
///   <term>two of <c>agent:</c>, <c>policy:</c>, <c>graph:</c>, or none of them</term>
///   <description>a load-time error</description>
/// </item>
/// </list>
/// <para>
/// Each entry selects one row, and each row is a <see cref="CompileTableRow"/>. This class selects
/// the row each entry picks and builds what every entry shares: the <c>agents.items</c> entries,
/// their tools, and the per-entry turn-disposition layers.
/// </para>
/// <para>
/// Every failure here reports through <see cref="ConfigurationLoadException"/> and carries a JSON
/// Pointer, exactly as the eight checks of section 8.5 do.
/// </para>
/// </remarks>
public static class ConfigurationCompiler
{
    /// <summary>Picks the row of the compile table one entry selects.</summary>
    /// <param name="entry">The entry being compiled.</param>
    /// <param name="entryPointer">The JSON Pointer to the entry, <c>/entries/&lt;name&gt;</c>.</param>
    /// <returns>The row.</returns>
    /// <exception cref="ConfigurationLoadException">The entry selects no row, or selects two.</exception>
    internal static CompileTableRow SelectEntryRow(EntryConfiguration entry, string entryPointer)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrEmpty(entryPointer);

        var hasAgent = entry.Agent is not null;
        var hasPolicy = entry.Policy is not null;
        var hasGraph = entry.Graph is not null;
        var selected = (hasAgent ? 1 : 0) + (hasPolicy ? 1 : 0) + (hasGraph ? 1 : 0);

        if (selected > 1)
        {
            throw Fail(
                entryPointer,
                "the entry holds two of agent:, policy:, and graph:. The section 8.2 compile table "
                + "takes exactly one: agent: for one agent answering directly, policy: for a conversation that "
                + "walks stages, and graph: for a run that needs checkpointing, a request port, or a "
                + "parallel fan-out with a join.");
        }

        if (hasGraph)
        {
            return SelectGraphRow(entry.Graph!, ConfigurationError.AppendPointer(entryPointer, "graph"));
        }

        if (hasPolicy)
        {
            return PolicyRow.Instance;
        }

        if (hasAgent)
        {
            return SelectSingleAgentRow(entry.Agent!, ConfigurationError.AppendPointer(entryPointer, "agent"));
        }

        throw Fail(
            entryPointer,
            "the entry holds none of agent:, policy:, and graph:, so it compiles to nothing.");
    }

    /// <summary>Picks the graph row: <c>pattern:</c> or <c>nodes:</c> and <c>edges:</c>, never both, never neither.</summary>
    private static CompileTableRow SelectGraphRow(GraphConfiguration graph, string graphPointer)
    {
        var hasPattern = graph.Pattern is not null;
        var hasNodes = graph.Nodes.Count > 0 || graph.Edges.Count > 0;

        if (hasPattern && hasNodes)
        {
            throw Fail(graphPointer, "the graph holds both pattern: and nodes:. It holds one or the other.");
        }

        if (hasPattern)
        {
            return PatternGraphRow.Instance;
        }

        if (hasNodes)
        {
            return ExplicitGraphRow.Instance;
        }

        throw Fail(graphPointer, "the graph declares neither pattern: nor nodes: and edges:.");
    }

    /// <summary>Picks the single-agent row, once <c>agent:</c> names an id.</summary>
    private static SingleAgentRow SelectSingleAgentRow(string agentId, string agentPointer)
    {
        if (agentId.Length == 0)
        {
            throw Fail(agentPointer, "the entry holds an empty agent:. It names one agents.items id.");
        }

        return SingleAgentRow.Instance;
    }

    /// <summary>Compiles one document into one agent per entry.</summary>
    /// <param name="configuration">The loaded document.</param>
    /// <param name="context">The seams the document names.</param>
    /// <returns>The compiled agents, keyed by entry name. Each is a process singleton: see T44.</returns>
    /// <exception cref="ConfigurationLoadException">An entry does not compile.</exception>
    public static IReadOnlyDictionary<string, CompiledAgent> CompileAll(
        AgentCoreConfiguration configuration,
        AgentCompilationContext context)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(context);

        IConversationStore conversations = context.ConversationStore ?? new InMemoryConversationStore();
        CompiledDocument document = new(configuration, conversations, new AgentCoreChatHistoryProvider(conversations));

        Dictionary<string, CompiledAgentSet> built = new(StringComparer.Ordinal);
        Dictionary<string, CompiledAgent> compiled = new(StringComparer.Ordinal);

        foreach (var (entryName, entry) in configuration.Entries)
        {
            var entryPointer = ConfigurationError.AppendPointer("/entries", entryName);
            var row = SelectEntryRow(entry, entryPointer);

            var shape = row.Shape;
            var key = shape == CompiledAgentShape.SingleAgent || shape == CompiledAgentShape.Policy ? "session" : shape.ToString();
            if (!built.TryGetValue(key, out var shared))
            {
                shared = BuildAgents(configuration, context, row.SessionCarriesHistory ? document.History : null);
                built[key] = shared;
            }

            var build = row.BuildEntry(configuration, entryName, entry, entryPointer, shared.Agents, context);

            TurnLayers layers = new(
                entry.FallbackReply ?? configuration.FallbackReply,
                entry.RefusalReply ?? configuration.RefusalReply,
                context.Moderation,
                row.SpokenAuthors(configuration, entry));

            compiled[entryName] = new CompiledAgentBuilder
            {
                Document = document,
                EntryName = entryName,
                Policy = entry.Policy,
                Row = row,
                Entry = build,
                Agents = shared,
                Layers = layers,
            }.Build();
        }

        return compiled;
    }

#pragma warning disable MAAI001 // BackgroundAgentsProvider is evaluation-only in Microsoft.Agents.AI 1.21.0.
    /// <summary>Builds one <c>ChatClientAgent</c> for each <c>agents.items</c> entry.</summary>
    /// <param name="configuration">The loaded document.</param>
    /// <param name="context">The seams the document names.</param>
    /// <param name="history">Store 1, or <see langword="null"/> to leave the framework default in place.</param>
    /// <returns>The agents keyed by id, with what every one of them declared.</returns>
    private static CompiledAgentSet BuildAgents(
        AgentCoreConfiguration configuration,
        AgentCompilationContext context,
        AgentCoreChatHistoryProvider? history)
    {
        Dictionary<string, AIAgent> agents = new(StringComparer.Ordinal);
        HashSet<string> harnessStateKeys = new(StringComparer.Ordinal);
        List<BackgroundAgentsProvider> backgroundProviders = [];
        if (configuration.Agents is not { } section)
        {
            return new CompiledAgentSet(agents, harnessStateKeys, backgroundProviders);
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

        AgentsWalk walk = new(
            section.Defaults,
            ResolvedClarification.From(configuration),
            configuration.Providers?.Knowledge?.Scope,
            backgroundProviders);

        foreach (var item in section.Items)
        {
            Resolve(item.Id);
        }

        return new CompiledAgentSet(agents, harnessStateKeys, backgroundProviders);

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
                    + "cycle, because the conversation would never return.");
            }

            path.Add(id);

            var compiledTools = AgentToolCompiler.Build(
                item, item.Model ?? section.Defaults?.Model, tools, context, pointer, Resolve);

            var providers = AgentContextProviderCompiler.Build(walk, item, context, pointer, Resolve);
            harnessStateKeys.UnionWith(AgentHarnessProviders.StateKeysOf(providers));
            harnessStateKeys.UnionWith(AgentApproval.StateKeysFor(section.Defaults, item, compiledTools));

            var built = new ChatClientAgent(
                WithToolFailureAuditing(context.ChatClients.GetChatClient(item.Model ?? section.Defaults?.Model)),
                new ChatClientAgentOptions
                {
                    Name = item.Id,
                    Description = item.Description,
                    ChatOptions = new ChatOptions
                    {
                        Instructions = AgentInstructions.Compose(section.Defaults, item),
                        Tools = compiledTools,
                    },
                    ChatHistoryProvider = history,
                    AIContextProviders = providers,
                });
            path.RemoveAt(path.Count - 1);

            var instrumented = new AIAgentBuilder(built)
                .UseOpenTelemetry(configure: static agent => agent.EnableSensitiveData = false)
                .Build();

            var approved = AgentApproval.Apply(instrumented, section.Defaults, item);
            var looped = AgentHarnessProviders.ApplyLoop(approved, section.Defaults, item, pointer);
            agents[id] = looped;
            return looped;
        }
    }
#pragma warning restore MAAI001

    /// <summary>Puts the auditing function-invocation loop into the pipeline of one agent.</summary>
    private static AuditingFunctionInvokingChatClient WithToolFailureAuditing(IChatClient model)
        => new(model.AsBuilder()
                    .UseOpenTelemetry(configure: static client => client.EnableSensitiveData = false)
                    .Use(static innerClient => new ModelFacingChatClient(innerClient))
                    .Build());

    internal static ConfigurationLoadException Fail(string pointer, string message)
        => new(new ConfigurationError
        {
            Pointer = pointer,
            Message = message,

            // The compile table is a shape rule over the whole document, like check 1.
            Check = ConfigurationCheck.DocumentSchema,
        });
}
