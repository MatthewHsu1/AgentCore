using AgentCore.Application.Calls.Memory;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Evaluation;
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
                + "takes exactly one: agent: for one agent answering directly, policy: for a call that "
                + "walks stages, and graph: for a run that needs checkpointing, a request port, or a "
                + "parallel fan-out with a join.");
        }

        if (hasGraph)
        {
            var graph = entry.Graph!;
            var graphPointer = ConfigurationError.AppendPointer(entryPointer, "graph");
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

        if (hasPolicy)
        {
            return PolicyRow.Instance;
        }

        if (hasAgent)
        {
            if (entry.Agent!.Length == 0)
            {
                throw Fail(
                    ConfigurationError.AppendPointer(entryPointer, "agent"),
                    "the entry holds an empty agent:. It names one agents.items id.");
            }

            return SingleAgentRow.Instance;
        }

        throw Fail(
            entryPointer,
            "the entry holds none of agent:, policy:, and graph:, so it compiles to nothing.");
    }

    /// <summary>Compiles one document into one agent per entry.</summary>
    /// <param name="configuration">The loaded document.</param>
    /// <param name="context">The seams the document names.</param>
    /// <returns>The compiled agents, keyed by entry name. Each is a process singleton: see T44.</returns>
    /// <exception cref="ConfigurationLoadException">An entry does not compile.</exception>
#pragma warning disable MAAI001 // BackgroundAgentsProvider is evaluation-only in Microsoft.Agents.AI 1.21.0.
    public static IReadOnlyDictionary<string, CompiledAgent> CompileAll(
        AgentCoreConfiguration configuration,
        AgentCompilationContext context)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(context);

        ICallStore calls = context.CallStore ?? new InMemoryCallStore();

        AgentCoreChatHistoryProvider history = new(calls);

        Dictionary<string, (Dictionary<string, AIAgent> Agents, IReadOnlySet<string> HarnessStateKeys, List<BackgroundAgentsProvider> BackgroundProviders)> built = new(StringComparer.Ordinal);

        Dictionary<string, CompiledAgent> compiled = new(StringComparer.Ordinal);

        foreach (var (entryName, entry) in configuration.Entries)
        {
            var entryPointer = ConfigurationError.AppendPointer("/entries", entryName);
            var row = SelectEntryRow(entry, entryPointer);

            var shape = row.Shape;
            var key = shape == CompiledAgentShape.SingleAgent || shape == CompiledAgentShape.Policy ? "session" : shape.ToString();
            if (!built.TryGetValue(key, out var shared))
            {
                shared = BuildAgents(configuration, context, row.SessionCarriesHistory ? history : null);
                built[key] = shared;
            }

            var (entryAgent, stages) = row.BuildEntry(configuration, entryName, entry, entryPointer, shared.Agents, context);

            var spokenBy = row.SpokenAuthors(configuration, entry);

            var fallbackReply = entry.FallbackReply ?? configuration.FallbackReply;
            var refusalReply = entry.RefusalReply ?? configuration.RefusalReply;

            compiled[entryName] = new CompiledAgent(
                configuration,
                entryName,
                entry.Policy,
                fallbackReply,
                refusalReply,
                row,
                calls,
                entryAgent,
                shared.Agents,
                stages,
                spokenBy,
                history,
                shared.HarnessStateKeys,
                shared.BackgroundProviders,
                inner => WithTurnDisposition(inner, fallbackReply, refusalReply, context.Moderation, spokenBy));
        }

        return compiled;
    }
#pragma warning restore MAAI001

    /// <summary>Puts the two turn-disposition layers on one agent a turn runs.</summary>
    /// <param name="agent">The compiled agent of one entry, or of one <c>policy:</c> stage.</param>
    /// <param name="fallbackReply">The resolved line the caller hears when a turn fails.</param>
    /// <param name="refusalReply">The resolved line the caller hears when the agent refuses to answer.</param>
    /// <param name="moderation">The endpoint seam, or <see langword="null"/> to moderate nothing.</param>
    /// <param name="spokenBy">The agents whose reply the caller hears, or <see langword="null"/> for all.</param>
    /// <returns>The agent the turn loop runs.</returns>
    private static AIAgent WithTurnDisposition(
        AIAgent agent,
        string fallbackReply,
        string refusalReply,
        PromptModerator? moderation,
        IReadOnlySet<string>? spokenBy)
    {
        AIAgent layered = new FallbackAgent(agent, fallbackReply, spokenBy);

        return moderation is null
            ? layered
            : new ModerationAgent(layered, moderation, refusalReply, ModerationAgent.DefaultTimeout);
    }

#pragma warning disable MAAI001 // BackgroundAgentsProvider is evaluation-only in Microsoft.Agents.AI 1.21.0.
    /// <summary>Builds one <c>ChatClientAgent</c> for each <c>agents.items</c> entry.</summary>
    /// <param name="configuration">The loaded document.</param>
    /// <param name="context">The seams the document names.</param>
    /// <param name="history">Store 1, or <see langword="null"/> to leave the framework default in place.</param>
    /// <returns>The agents keyed by id, the union of every harness provider's state keys, and every background provider for the call-end release.</returns>
    private static (Dictionary<string, AIAgent> Agents, IReadOnlySet<string> HarnessStateKeys, List<BackgroundAgentsProvider> BackgroundProviders) BuildAgents(
        AgentCoreConfiguration configuration,
        AgentCompilationContext context,
        AgentCoreChatHistoryProvider? history)
    {
        Dictionary<string, AIAgent> agents = new(StringComparer.Ordinal);
        HashSet<string> harnessStateKeys = new(StringComparer.Ordinal);
        List<BackgroundAgentsProvider> backgroundProviders = [];
        if (configuration.Agents is not { } section)
        {
            return (agents, harnessStateKeys, backgroundProviders);
        }

        var clarification = ResolvedClarification.From(configuration);

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

        return (agents, harnessStateKeys, backgroundProviders);

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

            var compiledTools = AgentToolCompiler.Build(
                item, item.Model ?? section.Defaults?.Model, tools, context, pointer, Resolve);

            var providers = AgentContextProviderCompiler.Build(
                section.Defaults,
                item,
                context,
                pointer,
                clarification,
                configuration.Providers?.Knowledge?.Scope,
                Resolve,
                backgroundProviders);
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
