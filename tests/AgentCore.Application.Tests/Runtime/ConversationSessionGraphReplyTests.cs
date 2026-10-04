using AgentCore.Application.Hooks;
using AgentCore.Application.Tests.Audit;
using AgentCore.TestSupport;
using AgentCore.Application.Audit.Memory;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime.Turn;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Domain.Audit;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Domain;
using AgentCore.Application.Runtime.Session;
using AgentCore.Application.Runtime.Turn.Lifecycle;

namespace AgentCore.Application.Tests.Runtime
{
    /// <summary>
    /// What the caller hears is one reply, on every compiled shape.
    /// </summary>
    public sealed class ConversationSessionGraphReplyTests
    {
        /// <summary>What the first node of the graph says while it works. The caller must not hear it.</summary>
        private const string Thinking = "Let me check the order system.";

        /// <summary>What the last node of the graph says. This is the whole spoken reply.</summary>
        private const string Spoken = "Order 41 ships Friday.";

        /// <summary>Two nodes, run in order. Both speak, and only the second one is an answer.</summary>
        private const string GraphYaml =
            """
          apiVersion: agentcore/v1
          agents:
            items:
              - { id: researcher, model: { ref: researcher }, instructions: "look things up" }
              - { id: responder,  model: { ref: responder },  instructions: "answer the caller" }
          entries:
            main:
              graph:
                pattern: sequential
                agents: [ researcher, responder ]
          """;

        /// <summary>One agent with one tool. A tool-calling turn returns three messages, not one.</summary>
        private const string ToolYaml =
            """
        apiVersion: agentcore/v1
        tools:
          - { id: lookup_order, kind: builtin, uses: orders.read, description: "Look up an order by its id." }
        agents:
          defaults:
            model: { ref: reply }
          items:
            - { id: only, instructions: "I answer everything", tools: [ lookup_order ] }
        entries:
          main:
            agent: only
        """;

        [Fact]
        public async Task CompleteTurn_GraphRow_SpeaksFinalNodeReplyOnly()
        {
            // Arrange. Each node answers with its own line, so a concatenated reply is visible.
            using ScriptedChatClient researcher = new(Thinking);
            using ScriptedChatClient responder = new(Spoken);
            InMemoryAuditSink sink = new();
            ConversationSession session = Build(GraphYaml, researcher, responder, sink).Create("conversation-graph");

            // Act.
            TurnResult turn = await session.RunTurnAsync("where is my order", TestContext.Current.CancellationToken);

            // Assert. The caller hears the answer, and the chain records the answer.
            Assert.Equal(Spoken, turn.ReplyText);
            Assert.DoesNotContain(Thinking, turn.ReplyText, StringComparison.Ordinal);

            AuditEvent completed = Assert.Single(
                await session.RowsAsync(sink),
                entry => entry.Kind == AuditEventKind.TurnCompleted);
            Assert.Equal(AuditHash.OfText(Spoken).Value, completed.Payload[AuditPayloadKeys.ReplyTextSha256]);
        }

        [Fact]
        public async Task CompleteTurn_ToolCallingTurn_SpeaksFinalAssistantMessage()
        {
            // Arrange. The model calls the tool once, reads the result, then answers.
            using ToolCallingChatClient reply = new(Spoken);
            InMemoryAuditSink sink = new();
            ConversationSession session = Build(ToolYaml, reply, null, sink, new StubToolBuilder(/*lang=json,strict*/ """{ "status": "shipped" }""").Create)
                .Create("conversation-tool");

            // Act.
            TurnResult turn = await session.RunTurnAsync("where is my order", TestContext.Current.CancellationToken);

            // Assert. A tool call and a tool result carry no text, so rows 1 and 2 are unchanged.
            Assert.Equal(["lookup_order"], reply.Called);
            Assert.Equal(Spoken, turn.ReplyText);

            AuditEvent completed = Assert.Single(
                await session.RowsAsync(sink),
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
            ConversationSession session = Build(GraphYaml, researcher, responder, sink).Create("conversation-quiet");

            // Act.
            TurnResult turn = await session.RunTurnAsync("where is my order", TestContext.Current.CancellationToken);

            // Assert. A quiet answer is silence on a voice conversation, so the turn speaks the fallback. It must
            // never fall back to the node before it, which is the graph thinking out loud.
            Assert.Equal(ConversationSession.FallbackReply, turn.ReplyText);
            Assert.Equal(TurnFailureReasons.EmptyReply, turn.Failure);
            Assert.DoesNotContain(Thinking, turn.ReplyText, StringComparison.Ordinal);

            AuditEvent completed = Assert.Single(
                await session.RowsAsync(sink),
                entry => entry.Kind == AuditEventKind.TurnCompleted);
            Assert.Equal(
                AuditHash.OfText(ConversationSession.FallbackReply).Value,
                completed.Payload[AuditPayloadKeys.ReplyTextSha256]);
        }

        [Fact]
        public async Task Streaming_GraphRowWhoseFinalNodeGoesQuiet_SpeaksTheFallback()
        {
            // Arrange. The same graph, on the run shape that hands each piece over as it arrives.
            using ScriptedChatClient researcher = new(Thinking);
            using ScriptedChatClient responder = new("   ");
            ConversationSession session = Build(GraphYaml, researcher, responder, new InMemoryAuditSink()).Create("conversation-quiet");

            // Act.
            List<string> spoken = [];
            await foreach (ChatResponseUpdate update in session.RunTurnStreamingAsync(
                "where is my order", TestContext.Current.CancellationToken))
            {
                spoken.Add(update.Text);
            }

            // Assert. The host hears one fallback and none of the deliberation. The quiet node's own
            // whitespace still passes the seam — a space between two words is speech, so the filter
            // cannot drop one — and it is inaudible.
            Assert.Equal(ConversationSession.FallbackReply, string.Concat(spoken).Trim());
            Assert.DoesNotContain(Thinking, string.Concat(spoken), StringComparison.Ordinal);
            Assert.NotNull(session.LastTurn);
            Assert.Equal(TurnFailureReasons.EmptyReply, session.LastTurn.Failure);
        }

        [Fact]
        public async Task Streaming_GraphRow_StreamsEveryFragmentOfTheFinalNodeAndNoneOfTheFirst()
        {
            // Arrange. The node that answers speaks in pieces, the way a real model streams.
            using ScriptedChatClient researcher = new(Thinking);
            using ScriptedChatClient responder = new("Order 41 ", "ships Friday.");
            ConversationSession session = Build(GraphYaml, researcher, responder, new InMemoryAuditSink()).Create("conversation-stream");

            // Act.
            List<string> spoken = [];
            await foreach (ChatResponseUpdate update in session.RunTurnStreamingAsync(
                "where is my order", TestContext.Current.CancellationToken))
            {
                spoken.Add(update.Text);
            }

            // Assert. Every piece of the answer reaches the host, and none of the deliberation does.
            Assert.Equal(Spoken, string.Concat(spoken));
            Assert.NotNull(session.LastTurn);
            Assert.Null(session.LastTurn.Failure);
        }

        [Fact]
        public async Task Streaming_GraphRow_EndsWithTheIdsOfItsAppend()
        {
            // Arrange. The committed ids ride the turn's last update, on a row whose
            // output is filtered to its final node as on any other.
            using ScriptedChatClient researcher = new(Thinking);
            using ScriptedChatClient responder = new(Spoken);
            ConversationSession session = Build(GraphYaml, researcher, responder, new InMemoryAuditSink()).Create("conversation-ids");

            // Act.
            List<ChatResponseUpdate> updates = [];
            await foreach (ChatResponseUpdate update in session.RunTurnStreamingAsync(
                "where is my order", TestContext.Current.CancellationToken))
            {
                updates.Add(update);
            }

            // Assert. The last update names what the append wrote, and the reply id is the last message written.
            TurnCommittedContent committed = Assert.Single(updates[^1].Contents.OfType<TurnCommittedContent>());
            Assert.NotNull(committed.ReplyMessageId);
            Assert.Equal(session.LastReplyMessageId, committed.ReplyMessageId);
        }

        /// <summary>Compiles one configuration over offline models and returns the session factory.</summary>
        /// <param name="yaml">The configuration to compile.</param>
        /// <param name="reply">The model every unrouted agent uses.</param>
        /// <param name="responder">The model routed to <c>responder</c>, if the graph needs a second one.</param>
        /// <param name="sink">Where the turn's events land.</param>
        /// <param name="tools">The tool factory, if the configuration declares a tool.</param>
        /// <returns>The factory.</returns>
        private static ConversationSessionFactory Build(
            string yaml,
            IChatClient reply,
            IChatClient? responder,
            IAuditSinkPort sink,
            Func<ToolConfiguration, AITool?>? tools = null)
        {
            AgentCoreConfiguration document = ConfigurationLoader.LoadYaml(yaml);
            RoutingChatClientFactory chatClients = new(reply);
            if (responder is not null)
            {
                _ = chatClients.Route("responder", responder);
            }

            CompiledAgent compiled = ConfigurationCompiler.CompileAll(
                document,
                new AgentCompilationContext(chatClients)
                {
                    Tools = TestToolRegistry.From(document, tools, TestContext.Current.CancellationToken),
                })["main"];

            return new ConversationSessionFactory(
                compiled,
                new GuardEvaluator(compiled.Configuration.Guards),
                hooks: BuiltInHooks.Create(sink));
        }
    }
}
