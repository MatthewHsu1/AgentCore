using System.Diagnostics;
using AgentCore.Application.Audit;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tests.Runtime;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Diagnostics;

/// <summary>
/// Task 6a: Microsoft.Extensions.AI's and Microsoft.Agents.AI's own GenAI instrumentation, switched on
/// for the first time. <c>AgentCoreTelemetry.cs</c> asserted both libraries "already emit" their spans
/// and metrics; before this task nothing ever called <c>UseOpenTelemetry</c>, so nothing did.
/// </summary>
/// <remarks>
/// <para>
/// Every test here proves something an <see cref="ActivityListener"/> or a direct
/// <c>GetService</c> probe observed, not merely that the wiring compiles.
/// <see cref="ConfigurationCompiler"/>'s <c>WithToolFailureAuditing</c> and <c>Resolve</c> remarks
/// explain WHERE the instrumentation sits and WHY; this file is the proof those remarks describe what
/// the compiled pipeline actually does.
/// </para>
/// <para>
/// Every agent id and call id here is unique per test run (a fresh <see cref="Guid"/>), because an
/// <see cref="ActivityListener"/> subscribes to the whole process and another test class may run
/// beside this one on the same two library sources.
/// </para>
/// </remarks>
public sealed class LibraryOpenTelemetryTests
{
    /// <summary>The default source name <c>OpenTelemetryChatClient</c> (Microsoft.Extensions.AI 10.8.3)
    /// resolves to when a caller names none. Read out of the restored assembly; see
    /// <c>ConfigurationCompiler.WithToolFailureAuditing</c>.</summary>
    private const string ChatSourceName = "Experimental.Microsoft.Extensions.AI";

    /// <summary>The default source name <c>OpenTelemetryAgent</c> (Microsoft.Agents.AI 1.17.0) resolves
    /// to when a caller names none. Read out of the restored assembly; see
    /// <c>ConfigurationCompiler.BuildAgents</c>.</summary>
    private const string AgentSourceName = "Experimental.Microsoft.Agents.AI";

    // -------------------------------------------------------------------------------------------
    // The ordering fix: execute_tool nests under invoke_agent, not beside it.
    // -------------------------------------------------------------------------------------------
    [Fact]
    public async Task AToolCallingTurn_NestsExecuteToolUnderInvokeAgentAndNotBesideIt()
    {
        var agentId = "agent-" + Guid.NewGuid().ToString("N");
        var yaml = $$"""
            apiVersion: agentcore/v1
            name: nesting-check
            tools:
              - { id: lookup_order, kind: builtin, uses: orders.read }
            agents:
              items:
                - { id: {{agentId}}, instructions: "answer questions", tools: [ lookup_order ] }
            """;

        List<Activity> spans = [];
        using var listener = ListenToLibrarySources(spans);

        using ToolCallingChatClient client = new("the order shipped.");
        StubToolFactory tools = new("""{ "status": "shipped" }""");

        var session = Build(yaml, client, tools).Create();

        await session.RunTurnAsync("where is my order", TestContext.Current.CancellationToken);

        var mine = Snapshot(spans);

        // OpenTelemetryAgent.RunCoreAsync relabels the span its own OpenTelemetryChatClient opens, so
        // the operation this test looks for lives on DisplayName ("invoke_agent {name}({id})"), not on
        // Activity.OperationName (which stays "chat" — verified by decompiling
        // Microsoft.Extensions.AI.OpenTelemetryChatClient.GetResponseAsync).
        var invokeAgent = Assert.Single(
            mine, span => span.DisplayName.StartsWith("invoke_agent " + agentId, StringComparison.Ordinal));

        // The listener subscribes to the whole process, and xUnit runs other test classes beside this
        // one that also compile agents on these same two default source names. Everything from here
        // down is scoped to children of THIS run's own invoke_agent (a per-test unique W3C span id),
        // so a concurrent test's spans on the same sources cannot leak into these counts.
        var underInvokeAgent = mine.Where(span => string.Equals(span.ParentId, invokeAgent.Id, StringComparison.Ordinal)).ToList();

        // FunctionInvocationProcessor names its own span "execute_tool {toolName}" directly through
        // ActivitySource.StartActivity, so OperationName is reliable here.
        var executeTool = Assert.Single(
            underInvokeAgent, span => span.OperationName.StartsWith("execute_tool", StringComparison.Ordinal));

        // The actual proof the ordering fix worked: execute_tool's parent IS invoke_agent, by object
        // identity and by W3C parent id — not a sibling of it, and not nested inside the whole
        // tool-calling loop as one "chat" span the way the broken ordering produced. See the "Chat
        // telemetry is wired here" remark on WithToolFailureAuditing for why the fix makes this true:
        // the per-round chat span (source "Experimental.Microsoft.Extensions.AI") closes before the
        // tool runs, so Activity.Current has reverted to invoke_agent by the time execute_tool opens.
        Assert.Same(invokeAgent, executeTool.Parent);

        // And the per-round chat spans exist too (one for the round that decided to call the tool, one
        // for the round that answered after the tool result), as siblings of execute_tool under the
        // same parent — not merged into the tool loop, and not swallowing it.
        var chatRounds = underInvokeAgent.Where(IsChatRoundSpan).ToList();
        Assert.Equal(2, chatRounds.Count);
    }

    // -------------------------------------------------------------------------------------------
    // Exactly once: every compiled agent is wrapped for OpenTelemetry, and none is wrapped twice.
    // -------------------------------------------------------------------------------------------
    [Fact]
    public async Task EveryCompiledAgent_IsInstrumentedExactlyOnce()
    {
        var frontId = "front-" + Guid.NewGuid().ToString("N");
        var specialistId = "specialist-" + Guid.NewGuid().ToString("N");
        var yaml = $$"""
            apiVersion: agentcore/v1
            name: exactly-once-check
            tools:
              - { id: ask_specialist, kind: agent, agent: {{specialistId}}, description: Ask the specialist. }
            agents:
              items:
                - { id: {{frontId}}, instructions: "the caller talks to me", tools: [ ask_specialist ] }
                - { id: {{specialistId}}, instructions: "I answer product questions" }
            policy:
              initial: talk
              stages:
                - { id: talk, agent: {{frontId}}, terminal: true }
            """;

        var document = ConfigurationLoader.LoadYaml(yaml);

        // Reachable structurally, with no turn run at all: ConfigurationCompiler.Resolve wraps the
        // agent it just built, once, immediately before it caches it in the dictionary every later
        // lookup (row 2's stage lookup, and the delegation tool's ResolveInner) reads back out of. A
        // second wrap would require Resolve to reach the "var built = new ChatClientAgent(...)" branch
        // twice for one id, and the early "agents.TryGetValue" return above it is what this test would
        // catch failing to hold.
        using ToolCallingChatClient buildOnlyClient = new("unused");
        var compiledOnly = ConfigurationCompiler.Compile(
            document, new AgentCompilationContext(new FakeChatClientFactory(buildOnlyClient)));

        Assert.IsType<OpenTelemetryAgent>(compiledOnly.Agents[frontId]);
        Assert.IsType<ChatClientAgent>(compiledOnly.Agents[frontId].GetService<ChatClientAgent>());
        Assert.IsType<OpenTelemetryAgent>(compiledOnly.Agents[specialistId]);
        Assert.IsType<ChatClientAgent>(compiledOnly.Agents[specialistId].GetService<ChatClientAgent>());

        // The behavioural half of the same proof: running a turn that calls through the delegation
        // path produces exactly one invoke_agent span for each agent. Two OpenTelemetryAgent layers
        // around the same ChatClientAgent would double whichever agent got wrapped twice; a missing
        // wrap would mean zero.
        List<Activity> spans = [];
        using var listener = ListenToLibrarySources(spans);

        // AsAIFunction() generates one required string argument named "query" (no parameters: is
        // declared on ask_specialist), so the model call must fill it or the delegation throws
        // ArgumentException before ever reaching the specialist agent.
        using ToolCallingChatClient client = new(
            "the specialist answer", new Dictionary<string, object?>(StringComparer.Ordinal) { ["query"] = "help me" });
        var session = Build(yaml, client, tools: null).Create();

        await session.RunTurnAsync("help me", TestContext.Current.CancellationToken);

        var mine = Snapshot(spans);

        Assert.Single(mine, span => span.DisplayName.StartsWith("invoke_agent " + frontId, StringComparison.Ordinal));
        Assert.Single(
            mine, span => span.DisplayName.StartsWith("invoke_agent " + specialistId, StringComparison.Ordinal));
    }

    // -------------------------------------------------------------------------------------------
    // The absolute constraint: EnableSensitiveData stays false on both the agent-level and the
    // chat-level instrumentation, regardless of what OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT
    // says in the environment this test happens to run in.
    // -------------------------------------------------------------------------------------------
    [Fact]
    public void EveryCompiledAgent_KeepsSensitiveDataCaptureOffOnBothLayers()
    {
        var agentId = "agent-" + Guid.NewGuid().ToString("N");
        var yaml = $$"""
            apiVersion: agentcore/v1
            name: no-sensitive-data
            agents:
              items:
                - { id: {{agentId}} }
            """;

        using ToolCallingChatClient client = new("hello");
        var compiled = ConfigurationCompiler.Compile(
            ConfigurationLoader.LoadYaml(yaml), new AgentCompilationContext(new FakeChatClientFactory(client)));

        var otelAgent = Assert.IsType<OpenTelemetryAgent>(compiled.Agent);

        // OpenTelemetryAgent.EnableSensitiveData otherwise defaults to
        // TelemetryHelpers.EnableSensitiveDataDefault, which reads
        // OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT from the environment. This repo carries
        // live customer phone calls, so ConfigurationCompiler forces it false explicitly rather than
        // trusting that variable to stay unset.
        Assert.False(otelAgent.EnableSensitiveData);

        // The chat-level layer WithToolFailureAuditing wires in below AuditingFunctionInvokingChatClient
        // is a second, independent OpenTelemetryChatClient instance with its own EnableSensitiveData,
        // and it is forced off the same way, at the same call site.
        var chatClient = compiled.Agent.GetService<OpenTelemetryChatClient>();
        Assert.NotNull(chatClient);
        Assert.False(chatClient.EnableSensitiveData);
    }

    // -------------------------------------------------------------------------------------------
    // gen_ai.conversation.id "for free": CallSession names the call as the conversation, and both
    // library layers pick it up through ChatOptions.ConversationId with no plumbing beyond that.
    // -------------------------------------------------------------------------------------------
    [Fact]
    public async Task ASingleAgentTurn_CarriesTheCallIdAsGenAiConversationIdOnBothLibrarySpans()
    {
        var agentId = "agent-" + Guid.NewGuid().ToString("N");
        var yaml = $$"""
            apiVersion: agentcore/v1
            name: conversation-id-check
            agents:
              items:
                - { id: {{agentId}} }
            """;

        List<Activity> spans = [];
        using var listener = ListenToLibrarySources(spans);

        using ToolCallingChatClient client = new("hello there.");
        var callId = "call-" + Guid.NewGuid().ToString("N");
        var session = Build(yaml, client, tools: null).Create(callId);

        await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

        var mine = Snapshot(spans);

        var invokeAgent = Assert.Single(
            mine, span => span.DisplayName.StartsWith("invoke_agent " + agentId, StringComparison.Ordinal));

        // Scoped to this run's own invoke_agent for the same reason as the nesting test above: other
        // test classes compile agents on these same two sources concurrently.
        var chat = Assert.Single(
            mine, span => IsChatRoundSpan(span) && string.Equals(span.ParentId, invokeAgent.Id, StringComparison.Ordinal));

        Assert.Equal(callId, invokeAgent.GetTagItem("gen_ai.conversation.id"));
        Assert.Equal(callId, chat.GetTagItem("gen_ai.conversation.id"));
    }

    // -------------------------------------------------------------------------------------------
    // Helpers.
    // -------------------------------------------------------------------------------------------
    /// <summary>
    /// Whether one span is a per-round model call and not the relabeled invoke_agent span.
    /// </summary>
    /// <remarks>
    /// <c>OpenTelemetryAgent</c> opens its own <c>invoke_agent</c> span through an internal
    /// <c>OpenTelemetryChatClient</c> instance, so that span's own <c>Activity.OperationName</c> is
    /// also <c>"chat"</c> — only its <c>DisplayName</c> is rewritten. A filter on <c>OperationName</c>
    /// alone therefore matches both the real per-round chat span this predicate looks for and the
    /// invoke_agent span beside it; excluding the renamed <c>DisplayName</c> is what tells them apart.
    /// </remarks>
    private static bool IsChatRoundSpan(Activity span)
        => span.OperationName.StartsWith("chat", StringComparison.Ordinal)
        && !span.DisplayName.StartsWith("invoke_agent", StringComparison.Ordinal);

    /// <summary>Subscribes to the two library sources this task turns on.</summary>
    private static ActivityListener ListenToLibrarySources(List<Activity> spans)
    {
        ActivityListener listener = new()
        {
            ShouldListenTo = source =>
                string.Equals(source.Name, ChatSourceName, StringComparison.Ordinal)
                || string.Equals(source.Name, AgentSourceName, StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                lock (spans)
                {
                    spans.Add(activity);
                }
            },
        };

        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    /// <summary>Copies what the listener has collected so far. See the sibling helper in
    /// <c>TurnObservabilityTests</c> for why a reader must not enumerate the live list.</summary>
    private static List<Activity> Snapshot(List<Activity> spans)
    {
        lock (spans)
        {
            return [.. spans];
        }
    }

    private static CallSessionFactory Build(string yaml, IChatClient client, IAgentToolFactory? tools)
    {
        var document = ConfigurationLoader.LoadYaml(yaml);
        FakeChatClientFactory factory = new(client);

        var compiled = ConfigurationCompiler.Compile(
            document, new AgentCompilationContext(factory) { Tools = tools });

        return new CallSessionFactory(
            compiled,
            new GuardEvaluator(compiled.Configuration.Guards),
            CallSessionFactory.CreateExtractor(compiled, factory),
            timeProvider: null,
            logger: null,
            CallObservers.Standard(new InMemoryAuditSink(), logger: null));
    }
}
