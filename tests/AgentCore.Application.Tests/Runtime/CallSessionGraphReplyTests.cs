using AgentCore.TestSupport;
using AgentCore.Application.Audit.Memory;
using AgentCore.Application.Audit;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Domain.Audit;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Runtime;

/// <summary>
/// What the caller hears is one reply, on every compiled shape.
/// </summary>
/// <remarks>
/// <para>
/// <c>AgentResponse.Text</c> concatenates every message of the response. On a graph row that is
/// every node's reply, so the caller hears the graph deliberating and the audit row records the
/// deliberation as the spoken words. The turn loop takes the last message instead.
/// </para>
/// <para>
/// Every test here runs offline. There is no network call and no API key in this file.
/// </para>
/// </remarks>
public sealed class CallSessionGraphReplyTests
{
    /// <summary>What the first node of the graph says while it works. The caller must not hear it.</summary>
    private const string Thinking = "Let me check the order system.";

    /// <summary>What the last node of the graph says. This is the whole spoken reply.</summary>
    private const string Spoken = "Order 41 ships Friday.";

    /// <summary>Two nodes, run in order. Both speak, and only the second one is an answer.</summary>
    private const string GraphYaml =
        """
        apiVersion: agentcore/v1
        name: two-node-graph
        agents:
          items:
            - { id: researcher, model: { ref: researcher }, instructions: "look things up" }
            - { id: responder,  model: { ref: responder },  instructions: "answer the caller" }
        graph:
          pattern: sequential
          agents: [ researcher, responder ]
        """;

    /// <summary>One agent with one tool. A tool-calling turn returns three messages, not one.</summary>
    private const string ToolYaml =
        """
        apiVersion: agentcore/v1
        name: tool-turn
        tools:
          - { id: lookup_order, kind: builtin, uses: orders.read, description: "Look up an order by its id." }
        agents:
          defaults:
            model: { ref: reply }
          items:
            - { id: only, instructions: "I answer everything", tools: [ lookup_order ] }
        """;

    [Fact]
    public async Task CompleteTurn_GraphRow_SpeaksFinalNodeReplyOnly()
    {
        // Arrange. Each node answers with its own line, so a concatenated reply is visible.
        using ScriptedChatClient researcher = new(Thinking);
        using ScriptedChatClient responder = new(Spoken);
        InMemoryAuditSink sink = new();
        var session = Build(GraphYaml, researcher, responder, sink).Create("call-graph");

        // Act.
        var turn = await session.RunTurnAsync("where is my order", TestContext.Current.CancellationToken);

        // Assert. The caller hears the answer, and the chain records the answer.
        Assert.Equal(Spoken, turn.ReplyText);
        Assert.DoesNotContain(Thinking, turn.ReplyText, StringComparison.Ordinal);

        var completed = Assert.Single(
            sink.EventsOf("call-graph"),
            entry => entry.Kind == AuditEventKind.TurnCompleted);
        Assert.Equal(AuditHash.OfText(Spoken).Value, completed.Payload[AuditPayloadKeys.ReplyTextSha256]);
    }

    [Fact]
    public async Task CompleteTurn_ToolCallingTurn_SpeaksFinalAssistantMessage()
    {
        // Arrange. The model calls the tool once, reads the result, then answers.
        using ToolCallingChatClient reply = new(Spoken);
        InMemoryAuditSink sink = new();
        var session = Build(ToolYaml, reply, null, sink, new StubToolBuilder("""{ "status": "shipped" }""").Create)
            .Create("call-tool");

        // Act.
        var turn = await session.RunTurnAsync("where is my order", TestContext.Current.CancellationToken);

        // Assert. A tool call and a tool result carry no text, so rows 1 and 2 are unchanged.
        Assert.Equal(["lookup_order"], reply.Called);
        Assert.Equal(Spoken, turn.ReplyText);

        var completed = Assert.Single(
            sink.EventsOf("call-tool"),
            entry => entry.Kind == AuditEventKind.TurnCompleted);
        Assert.Equal(AuditHash.OfText(Spoken).Value, completed.Payload[AuditPayloadKeys.ReplyTextSha256]);
    }

    [Fact]
    public async Task CompleteTurn_GraphRowWhoseFinalNodeGoesQuiet_SpeaksTheFallback()
    {
        // Arrange. The node that answers the caller says nothing at all.
        using ScriptedChatClient researcher = new(Thinking);
        using ScriptedChatClient responder = new("   ");
        InMemoryAuditSink sink = new();
        var session = Build(GraphYaml, researcher, responder, sink).Create("call-quiet");

        // Act.
        var turn = await session.RunTurnAsync("where is my order", TestContext.Current.CancellationToken);

        // Assert. A quiet answer is silence on a voice call, so the turn speaks the fallback. It must
        // never fall back to the node before it, which is the graph thinking out loud.
        Assert.Equal(CallSession.FallbackReply, turn.ReplyText);
        Assert.Equal(CallSession.EmptyReplyReason, turn.Failure);
        Assert.DoesNotContain(Thinking, turn.ReplyText, StringComparison.Ordinal);

        var completed = Assert.Single(
            sink.EventsOf("call-quiet"),
            entry => entry.Kind == AuditEventKind.TurnCompleted);
        Assert.Equal(
            AuditHash.OfText(CallSession.FallbackReply).Value,
            completed.Payload[AuditPayloadKeys.ReplyTextSha256]);
    }

    [Fact]
    public async Task Streaming_GraphRowWhoseFinalNodeGoesQuiet_SpeaksTheFallback()
    {
        // Arrange. The same graph, on the run shape that hands each piece over as it arrives.
        using ScriptedChatClient researcher = new(Thinking);
        using ScriptedChatClient responder = new("   ");
        var session = Build(GraphYaml, researcher, responder, new InMemoryAuditSink()).Create("call-quiet");

        // Act.
        List<string> spoken = [];
        await foreach (var update in session.RunTurnStreamingAsync(
            "where is my order", TestContext.Current.CancellationToken))
        {
            spoken.Add(update.Text);
        }

        // Assert. The host hears one fallback and none of the deliberation. The quiet node's own
        // whitespace still passes the seam — a space between two words is speech, so the filter
        // cannot drop one — and it is inaudible.
        Assert.Equal(CallSession.FallbackReply, string.Concat(spoken).Trim());
        Assert.DoesNotContain(Thinking, string.Concat(spoken), StringComparison.Ordinal);
        Assert.NotNull(session.LastTurn);
        Assert.Equal(CallSession.EmptyReplyReason, session.LastTurn.Failure);
    }

    [Fact]
    public async Task Streaming_GraphRow_StreamsEveryFragmentOfTheFinalNodeAndNoneOfTheFirst()
    {
        // Arrange. The node that answers speaks in pieces, the way a real model streams.
        using ScriptedChatClient researcher = new(Thinking);
        using ScriptedChatClient responder = new("Order 41 ", "ships Friday.");
        var session = Build(GraphYaml, researcher, responder, new InMemoryAuditSink()).Create("call-stream");

        // Act.
        List<string> spoken = [];
        await foreach (var update in session.RunTurnStreamingAsync(
            "where is my order", TestContext.Current.CancellationToken))
        {
            spoken.Add(update.Text);
        }

        // Assert. Every piece of the answer reaches the host, and none of the deliberation does.
        Assert.Equal(Spoken, string.Concat(spoken));
        Assert.NotNull(session.LastTurn);
        Assert.Null(session.LastTurn.Failure);
    }

    /// <summary>Compiles one configuration over offline models and returns the session factory.</summary>
    /// <param name="yaml">The configuration to compile.</param>
    /// <param name="reply">The model every unrouted agent uses.</param>
    /// <param name="responder">The model routed to <c>responder</c>, if the graph needs a second one.</param>
    /// <param name="sink">Where the turn's events land.</param>
    /// <param name="tools">The tool factory, if the configuration declares a tool.</param>
    /// <returns>The factory.</returns>
    private static CallSessionFactory Build(
        string yaml,
        IChatClient reply,
        IChatClient? responder,
        IAuditSinkPort sink,
        Func<ToolConfiguration, AITool?>? tools = null)
    {
        var document = ConfigurationLoader.LoadYaml(yaml);
        RoutingChatClientFactory chatClients = new(reply);
        if (responder is not null)
        {
            chatClients.Route("responder", responder);
        }

        var compiled = ConfigurationCompiler.Compile(
            document,
            new AgentCompilationContext(chatClients)
            {
                Tools = TestToolRegistry.From(document, tools, TestContext.Current.CancellationToken),
            });

        return new CallSessionFactory(
            compiled,
            new GuardEvaluator(compiled.Configuration.Guards),
            observers: CallObservers.Standard(sink, logger: null));
    }
}
