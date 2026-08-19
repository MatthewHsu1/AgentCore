using AgentCore.Application.Audit;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Evaluation;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Tests.Evaluation.Fakes;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Domain.Audit;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Runtime;

/// <summary>
/// Every audible turn is an ordinary successful run.
/// </summary>
/// <remarks>
/// <para>
/// R1, R2 and R3 used to be branches of the turn loop. They are two chat pipeline layers now:
/// <c>ModerationChatClient</c> refuses a flagged turn before the model runs, and
/// <c>FallbackChatClient</c> answers a run that threw or produced no text. Both report what they did
/// on a <c>TurnDisposition</c>, and the turn loop reads it to raise the rows it always raised.
/// </para>
/// <para>Every test here runs offline. There is no network call and no API key in this file.</para>
/// </remarks>
public sealed class TurnPipelineTests
{
    private const string RefusalReply = "I am sorry. I cannot help with that request.";

    private const string PlainYaml =
        """
        apiVersion: agentcore/v1
        name: pipeline
        refusalReply: "I am sorry. I cannot help with that request."
        agents:
          defaults:
            model: { ref: reply }
          items:
            - { id: only, instructions: "I answer everything" }
        """;

    private const string ToolYaml =
        """
        apiVersion: agentcore/v1
        name: pipeline-tools
        refusalReply: "I am sorry. I cannot help with that request."
        tools:
          - { id: lookup_order, kind: builtin, uses: orders.read }
        agents:
          defaults:
            model: { ref: reply }
          items:
            - { id: only, instructions: "I answer everything", tools: [ lookup_order ] }
        """;

    // -------------------------------------------------------------------------------------------
    // R3 — moderation judges the caller, and a flagged turn never reaches the model.
    // -------------------------------------------------------------------------------------------
    [Fact]
    public async Task Run_FlaggedInput_CompletesWithRefusalReply()
    {
        // Arrange.
        using SequencedChatClient model = new("never spoken");
        var session = Build(PlainYaml, model, moderation: ScriptedModerationEvaluator.Flagging("hate"))
            .Create("call-1");

        // Act.
        var turn = await session.RunTurnAsync("...", TestContext.Current.CancellationToken);

        // Assert. The run succeeded, so nothing threw and the turn is not a failure.
        Assert.Equal(RefusalReply, turn.ReplyText);
        Assert.Null(turn.Failure);
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task RunStreaming_FlaggedInput_CompletesWithRefusalReply()
    {
        // Arrange.
        using SequencedChatClient model = new("never spoken");
        var session = Build(PlainYaml, model, moderation: ScriptedModerationEvaluator.Flagging("hate"))
            .Create("call-1");

        // Act. The refusal arrives on the ordinary stream path, not on a branch of its own.
        List<string> spoken = [];
        await foreach (var update in session.RunTurnStreamingAsync("...", TestContext.Current.CancellationToken))
        {
            spoken.Add(update.Text);
        }

        // Assert.
        Assert.Equal(RefusalReply, string.Concat(spoken));
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task Run_FlaggedInput_InvokesNoTools()
    {
        // Arrange. The model would call the tool if it ever ran.
        using ToolCallingChatClient model = new("never spoken");
        StubToolFactory tools = new("""{ "status": "shipped" }""");
        var session = Build(ToolYaml, model, tools: tools, moderation: ScriptedModerationEvaluator.Flagging("hate"))
            .Create("call-1");

        // Act.
        await session.RunTurnAsync("...", TestContext.Current.CancellationToken);

        // Assert. The refusal is returned above the function-invoking loop, so no tool ran.
        Assert.Empty(model.Called);
    }

    // -------------------------------------------------------------------------------------------
    // R1 and R2 — a failed turn and a quiet turn both speak the fallback.
    // -------------------------------------------------------------------------------------------
    [Fact]
    public async Task Run_ModelThrows_CompletesWithFallbackReply()
    {
        // Arrange.
        using ThrowingChatClient model = new(new InvalidOperationException("the vendor is down"));
        var session = Build(PlainYaml, model).Create("call-1");

        // Act. Nothing escapes: the layer below the agent caught it.
        var turn = await session.RunTurnAsync("hello", TestContext.Current.CancellationToken);

        // Assert.
        Assert.Equal(CallSession.FallbackReply, turn.ReplyText);
        Assert.NotNull(turn.Failure);
        Assert.Contains("the vendor is down", turn.Failure, StringComparison.Ordinal);
        Assert.False(session.IsComplete);
    }

    [Fact]
    public async Task Run_EmptyModelReply_CompletesWithFallbackReply()
    {
        // Arrange. Request 41 goes out with no tools and returns quietly.
        using SequencedChatClient model = new("   ");
        var session = Build(PlainYaml, model).Create("call-1");

        // Act.
        var turn = await session.RunTurnAsync("hello", TestContext.Current.CancellationToken);

        // Assert.
        Assert.Equal(CallSession.FallbackReply, turn.ReplyText);
        Assert.Equal(CallSession.EmptyReplyReason, turn.Failure);
    }

    [Fact]
    public async Task Run_ModerationEndpointDown_RunsTurnAndReportsUnavailable()
    {
        // Arrange. A vendor outage must not refuse every caller on a support line.
        InMemoryAuditSink sink = new();
        using SequencedChatClient model = new("the ordinary reply");
        var session = Build(
                PlainYaml,
                model,
                sink: sink,
                moderation: ScriptedModerationEvaluator.Throwing(new InvalidOperationException("boom")))
            .Create("call-1");

        // Act.
        var turn = await session.RunTurnAsync("hello", TestContext.Current.CancellationToken);

        // Assert. The turn ran unchecked rather than being refused.
        Assert.Equal("the ordinary reply", turn.ReplyText);
        Assert.Equal(1, model.Calls);
        Assert.DoesNotContain(sink.EventsOf("call-1"), entry => entry.Kind == AuditEventKind.PromptFlagged);
    }

    // -------------------------------------------------------------------------------------------
    // The chain is unchanged. The layers moved; the rows did not.
    // -------------------------------------------------------------------------------------------
    [Fact]
    public async Task Run_FlaggedOutcome_RaisesSameAuditRowsAsBefore()
    {
        // Arrange.
        InMemoryAuditSink sink = new();
        var session = Build(PlainYaml, new SequencedChatClient("never spoken"), sink: sink,
            moderation: ScriptedModerationEvaluator.Flagging("violence", "harassment")).Create("call-1");

        // Act.
        await session.RunTurnAsync("...", TestContext.Current.CancellationToken);

        // Assert. call.started, prompt.flagged, turn.completed — the flag still takes the lower
        // ordinal, because the verdict is known before the model runs.
        var events = sink.EventsOf("call-1");
        Assert.Equal(
            [AuditEventKind.CallStarted, AuditEventKind.PromptFlagged, AuditEventKind.TurnCompleted],
            events.Select(entry => entry.Kind));
        Assert.Equal([0, 1, 2], events.Select(entry => entry.Sequence));
        Assert.Equal("violence,harassment", events[1].Payload[AuditPayloadKeys.ModerationCategories]);
    }

    [Fact]
    public async Task Run_ThrownOutcome_RaisesSameAuditRowsAsBefore()
    {
        // Arrange.
        InMemoryAuditSink sink = new();
        using ThrowingChatClient model = new(new InvalidOperationException("the vendor is down"));
        var session = Build(PlainYaml, model, sink: sink).Create("call-1");

        // Act.
        await session.RunTurnAsync("hello", TestContext.Current.CancellationToken);

        // Assert. The turn-altitude tool.failed row still names the fault, and still precedes the
        // turn.completed of the same turn.
        var events = sink.EventsOf("call-1");
        Assert.Equal(
            [AuditEventKind.CallStarted, AuditEventKind.ToolFailed, AuditEventKind.TurnCompleted],
            events.Select(entry => entry.Kind));
        Assert.Equal([0, 1, 2], events.Select(entry => entry.Sequence));
        Assert.Contains("the vendor is down", events[1].Payload[AuditPayloadKeys.ToolError], StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------------------------
    // Where the marker is readable. The read site differs by run shape, and that is measured.
    // -------------------------------------------------------------------------------------------
    [Fact]
    public async Task Run_BufferedTurn_TurnDispositionReadableOnResponse()
    {
        // Arrange.
        using SequencedChatClient model = new("the ordinary reply");
        var compiled = Compile(PlainYaml, model, out _, moderation: ScriptedModerationEvaluator.Clean());

        // Act.
        var response = await compiled.TurnAgent.RunAsync(
            "hello", cancellationToken: TestContext.Current.CancellationToken);

        // Assert. The marker rides the run itself, so nothing about the reply is disturbed.
        var properties = response.AdditionalProperties;
        Assert.NotNull(properties);
        Assert.True(AdditionalPropertiesExtensions.TryGetValue<TurnDisposition>(properties, out var disposition));
        Assert.Equal(ModerationOutcome.Clean, disposition!.Moderation);
    }

    [Fact]
    public async Task RunStreaming_CleanTurn_MarkerOnLeadingUpdateAddsNoText()
    {
        // Arrange.
        using SequencedChatClient model = new("the ordinary reply");
        var compiled = Compile(PlainYaml, model, out _, moderation: ScriptedModerationEvaluator.Clean());

        // Act.
        List<AgentResponseUpdate> updates = [];
        await foreach (var update in compiled.TurnAgent.RunStreamingAsync(
            "hello", cancellationToken: TestContext.Current.CancellationToken))
        {
            updates.Add(update);
        }

        // Assert. The marker rides an update with empty contents, so the caller's audio is untouched.
        Assert.Equal("the ordinary reply", string.Concat(updates.Select(update => update.Text)));
        Assert.Contains(
            updates,
            update => update.AdditionalProperties is { } properties
                && AdditionalPropertiesExtensions.Contains<TurnDisposition>(properties));
    }

    // -------------------------------------------------------------------------------------------
    // The layers wrap the agent a TURN runs, and never a graph node.
    // -------------------------------------------------------------------------------------------
    [Fact]
    public async Task Run_GraphRow_ModeratesTheCallerOnceAndNotOncePerNode()
    {
        // Arrange. Two nodes run for one turn.
        using SequencedChatClient researcher = new("Let me check the order system.");
        using SequencedChatClient responder = new("Order 41 ships Friday.");
        var endpoint = ScriptedModerationEvaluator.Clean();
        var session = BuildGraph(researcher, responder, endpoint).Create("call-1");

        // Act.
        await session.RunTurnAsync("where is my order", TestContext.Current.CancellationToken);

        // Assert. R3 is a rule about a turn, and one turn is one run of one agent on every row.
        Assert.Equal(["where is my order"], endpoint.Moderated);
        Assert.Equal(1, researcher.Calls);
        Assert.Equal(1, responder.Calls);
    }

    [Fact]
    public async Task Run_GraphRowWhereNoNodeSpeaks_SpeaksTheFallbackOnceForTheTurn()
    {
        // Arrange. Every node runs and none of them produces a word.
        using SequencedChatClient researcher = new("   ");
        using SequencedChatClient responder = new("   ");
        var session = BuildGraph(researcher, responder, moderation: null).Create("call-1");

        // Act.
        var turn = await session.RunTurnAsync("where is my order", TestContext.Current.CancellationToken);

        // Assert. R2 is a rule about a turn: one fallback is spoken to the caller, and none of it is
        // fed back into the graph as a node reply.
        Assert.Equal(CallSession.FallbackReply, turn.ReplyText);
        Assert.Equal(CallSession.EmptyReplyReason, turn.Failure);
        Assert.Equal(1, researcher.Calls);
        Assert.Equal(1, responder.Calls);
    }

    // -------------------------------------------------------------------------------------------
    // Helpers.
    // -------------------------------------------------------------------------------------------
    private const string GraphYaml =
        """
        apiVersion: agentcore/v1
        name: pipeline-graph
        agents:
          items:
            - { id: researcher, model: { ref: researcher }, instructions: "look things up" }
            - { id: responder,  model: { ref: responder },  instructions: "answer the caller" }
        graph:
          pattern: sequential
          agents: [ researcher, responder ]
        """;

    private static CallSessionFactory BuildGraph(
        IChatClient researcher,
        IChatClient responder,
        ScriptedModerationEvaluator? moderation)
    {
        RoutingChatClientFactory chatClients = new(researcher);
        chatClients.Route("responder", responder);

        var compiled = ConfigurationCompiler.Compile(
            ConfigurationLoader.LoadYaml(GraphYaml),
            new AgentCompilationContext(chatClients)
            {
                Moderation = moderation is null ? null : new PromptModerator(moderation),
            });

        return new CallSessionFactory(
            compiled,
            new GuardEvaluator(compiled.Configuration.Guards),
            observers: CallObservers.Standard(new InMemoryAuditSink(), logger: null));
    }

    private static CompiledAgent Compile(
        string yaml,
        IChatClient reply,
        out RoutingChatClientFactory chatClients,
        IAgentToolFactory? tools = null,
        ScriptedModerationEvaluator? moderation = null)
    {
        var document = ConfigurationLoader.LoadYaml(yaml);
        chatClients = new RoutingChatClientFactory(reply);

        return ConfigurationCompiler.Compile(
            document,
            new AgentCompilationContext(chatClients)
            {
                Tools = tools,
                Moderation = moderation is null ? null : new PromptModerator(moderation),
            });
    }

    private static CallSessionFactory Build(
        string yaml,
        IChatClient reply,
        IAuditSinkPort? sink = null,
        IAgentToolFactory? tools = null,
        ScriptedModerationEvaluator? moderation = null)
    {
        var compiled = Compile(yaml, reply, out _, tools, moderation);

        return new CallSessionFactory(
            compiled,
            new GuardEvaluator(compiled.Configuration.Guards),
            observers: CallObservers.Standard(sink ?? new InMemoryAuditSink(), logger: null));
    }
}
