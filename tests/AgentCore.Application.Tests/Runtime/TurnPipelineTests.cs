using AgentCore.Application.Hooks;
using AgentCore.Application.Tests.Audit;
using AgentCore.TestSupport;
using AgentCore.Application.Audit.Memory;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Evaluation;
using AgentCore.Application.Ports;
using AgentCore.Application.Tests.Evaluation.Fakes;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Domain.Audit;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Domain;
using AgentCore.Application.Runtime.Session;
using AgentCore.Application.Runtime.Turn.Lifecycle;

namespace AgentCore.Application.Tests.Runtime
{
    /// <summary>
    /// Every audible turn is an ordinary successful run.
    /// </summary>
    public sealed class TurnPipelineTests
    {
        private const string RefusalReply = "I am sorry. I cannot help with that request.";

        private const string PlainYaml =
            """
        apiVersion: agentcore/v1
        refusalReply: "I am sorry. I cannot help with that request."
        agents:
          defaults:
            model: { ref: reply }
          items:
            - { id: only, instructions: "I answer everything" }
        entries:
          main:
            agent: only
        """;

        private const string ToolYaml =
            """
        apiVersion: agentcore/v1
        refusalReply: "I am sorry. I cannot help with that request."
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
        public async Task Run_FlaggedInput_CompletesWithRefusalReply()
        {
            using SequencedChatClient model = new("never spoken");
            ConversationSession session = Build(PlainYaml, model, moderation: ScriptedModerationEvaluator.Flagging("hate"))
                .Create("conversation-1");

            TurnResult turn = await session.RunTurnAsync("...", TestContext.Current.CancellationToken);

            // The run succeeded, so nothing threw and the turn is not a failure.
            Assert.Equal(RefusalReply, turn.ReplyText);
            Assert.Null(turn.Failure);
            Assert.Equal(0, model.Calls);
        }

        [Fact]
        public async Task RunStreaming_FlaggedInput_CompletesWithRefusalReply()
        {
            using SequencedChatClient model = new("never spoken");
            ConversationSession session = Build(PlainYaml, model, moderation: ScriptedModerationEvaluator.Flagging("hate"))
                .Create("conversation-1");

            // The refusal arrives on the ordinary stream path, not on a branch of its own.
            List<string> spoken = [];
            await foreach (ChatResponseUpdate update in session.RunTurnStreamingAsync("...", TestContext.Current.CancellationToken))
            {
                spoken.Add(update.Text);
            }

            Assert.Equal(RefusalReply, string.Concat(spoken));
            Assert.Equal(0, model.Calls);
        }

        [Fact]
        public async Task Run_FlaggedInput_InvokesNoTools()
        {
            // The model would call the tool if it ever ran.
            using ToolCallingChatClient model = new("never spoken");
            StubToolBuilder tools = new(/*lang=json,strict*/ """{ "status": "shipped" }""");
            ConversationSession session = Build(ToolYaml, model, tools: tools.Create, moderation: ScriptedModerationEvaluator.Flagging("hate"))
                .Create("conversation-1");

            _ = await session.RunTurnAsync("...", TestContext.Current.CancellationToken);

            // The refusal is returned above the function-invoking loop, so no tool ran.
            Assert.Empty(model.Called);
        }

        [Fact]
        public async Task Run_ModelThrows_CompletesWithFallbackReply()
        {
            using ThrowingChatClient model = new(new InvalidOperationException("the vendor is down"));
            ConversationSession session = Build(PlainYaml, model).Create("conversation-1");

            // Nothing escapes: the layer below the agent caught it.
            TurnResult turn = await session.RunTurnAsync("hello", TestContext.Current.CancellationToken);

            Assert.Equal(ConversationSession.FallbackReply, turn.ReplyText);
            Assert.NotNull(turn.Failure);
            Assert.Contains("the vendor is down", turn.Failure, StringComparison.Ordinal);
            Assert.False(session.IsComplete);
        }

        [Fact]
        public async Task Run_EmptyModelReply_CompletesWithFallbackReply()
        {
            // Request 41 goes out with no tools and returns quietly.
            using SequencedChatClient model = new("   ");
            ConversationSession session = Build(PlainYaml, model).Create("conversation-1");

            TurnResult turn = await session.RunTurnAsync("hello", TestContext.Current.CancellationToken);

            Assert.Equal(ConversationSession.FallbackReply, turn.ReplyText);
            Assert.Equal(TurnFailureReasons.EmptyReply, turn.Failure);
        }

        [Fact]
        public async Task Run_ModerationEndpointDown_RunsTurnAndReportsUnavailable()
        {
            // A vendor outage must not refuse every caller on a support line.
            InMemoryAuditSink sink = new();
            using SequencedChatClient model = new("the ordinary reply");
            ConversationSession session = Build(
                    PlainYaml,
                    model,
                    sink: sink,
                    moderation: ScriptedModerationEvaluator.Throwing(new InvalidOperationException("boom")))
                .Create("conversation-1");

            TurnResult turn = await session.RunTurnAsync("hello", TestContext.Current.CancellationToken);

            // The turn ran unchecked rather than being refused.
            Assert.Equal("the ordinary reply", turn.ReplyText);
            Assert.Equal(1, model.Calls);
            Assert.DoesNotContain(await session.RowsAsync(sink), entry => entry.Kind == AuditEventKind.PromptFlagged);
        }

        [Fact]
        public async Task Run_FlaggedOutcome_RaisesSameAuditRowsAsBefore()
        {
            InMemoryAuditSink sink = new();
            ConversationSession session = Build(PlainYaml, new SequencedChatClient("never spoken"), sink: sink,
                moderation: ScriptedModerationEvaluator.Flagging("violence", "harassment")).Create("conversation-1");

            _ = await session.RunTurnAsync("...", TestContext.Current.CancellationToken);

            // conversation.started, prompt.flagged, turn.completed — the flag still precedes the turn
            // event, because the verdict is known before the model runs.
            IReadOnlyList<AuditEvent> events = await session.RowsAsync(sink);
            Assert.Equal(
                [AuditEventKind.ConversationStarted, AuditEventKind.PromptFlagged, AuditEventKind.TurnCompleted],
                events.Select(entry => entry.Kind));
            Assert.Equal("violence,harassment", events[1].Payload[AuditPayloadKeys.ModerationCategories]);
        }

        [Fact]
        public async Task Run_ThrownOutcome_WritesNoToolFailedRow()
        {
            InMemoryAuditSink sink = new();
            using ThrowingChatClient model = new(new InvalidOperationException("the vendor is down"));
            ConversationSession session = Build(PlainYaml, model, sink: sink).Create("conversation-1");

            TurnResult turn = await session.RunTurnAsync("hello", TestContext.Current.CancellationToken);

            // The model threw, not a tool, so the turn fails as a run fault and names no tool.
            IReadOnlyList<AuditEvent> events = await session.RowsAsync(sink);
            Assert.Equal(
                [AuditEventKind.ConversationStarted, AuditEventKind.TurnCompleted],
                events.Select(entry => entry.Kind));
            Assert.Equal("the turn's run faulted, so it spoke the fallback. the vendor is down", turn.Failure);
        }

        [Fact]
        public async Task Run_BufferedTurn_TurnDispositionReadableOnResponse()
        {
            using SequencedChatClient model = new("the ordinary reply");
            CompiledAgent compiled = Compile(PlainYaml, model, out _, moderation: ScriptedModerationEvaluator.Clean());

            AgentResponse response = await compiled.TurnAgent.RunAsync(
                "hello", cancellationToken: TestContext.Current.CancellationToken);

            // The marker rides the run itself, so nothing about the reply is disturbed.
            AdditionalPropertiesDictionary? properties = response.AdditionalProperties;
            Assert.NotNull(properties);
            Assert.True(AdditionalPropertiesExtensions.TryGetValue(properties, out TurnDisposition? disposition));
            Assert.Equal(ModerationOutcome.Clean, disposition!.Moderation);
        }

        [Fact]
        public async Task RunStreaming_CleanTurn_MarkerOnLeadingUpdateAddsNoText()
        {
            using SequencedChatClient model = new("the ordinary reply");
            CompiledAgent compiled = Compile(PlainYaml, model, out _, moderation: ScriptedModerationEvaluator.Clean());

            List<AgentResponseUpdate> updates = [];
            await foreach (AgentResponseUpdate update in compiled.TurnAgent.RunStreamingAsync(
                "hello", cancellationToken: TestContext.Current.CancellationToken))
            {
                updates.Add(update);
            }

            // The marker rides an update with empty contents, so the caller's audio is untouched.
            Assert.Equal("the ordinary reply", string.Concat(updates.Select(update => update.Text)));
            Assert.Contains(
                updates,
                update => update.AdditionalProperties is { } properties
                    && AdditionalPropertiesExtensions.Contains<TurnDisposition>(properties));
        }

        [Fact]
        public async Task Run_GraphRow_ModeratesTheCallerOnceAndNotOncePerNode()
        {
            // Two nodes run for one turn.
            using SequencedChatClient researcher = new("Let me check the order system.");
            using SequencedChatClient responder = new("Order 41 ships Friday.");
            ScriptedModerationEvaluator endpoint = ScriptedModerationEvaluator.Clean();
            ConversationSession session = BuildGraph(researcher, responder, endpoint).Create("conversation-1");

            _ = await session.RunTurnAsync("where is my order", TestContext.Current.CancellationToken);

            // Moderation is a rule about a turn, and one turn is one run of one agent on every row.
            Assert.Equal(["where is my order"], endpoint.Moderated);
            Assert.Equal(1, researcher.Calls);
            Assert.Equal(1, responder.Calls);
        }

        [Fact]
        public async Task Run_GraphRowWhereNoNodeSpeaks_SpeaksTheFallbackOnceForTheTurn()
        {
            // Every node runs and none of them produces a word.
            using SequencedChatClient researcher = new("   ");
            using SequencedChatClient responder = new("   ");
            ConversationSession session = BuildGraph(researcher, responder, moderation: null).Create("conversation-1");

            TurnResult turn = await session.RunTurnAsync("where is my order", TestContext.Current.CancellationToken);

            // The fallback is a rule about a turn: one fallback is spoken to the caller, and none of it is
            // fed back into the graph as a node reply.
            Assert.Equal(ConversationSession.FallbackReply, turn.ReplyText);
            Assert.Equal(TurnFailureReasons.EmptyReply, turn.Failure);
            Assert.Equal(1, researcher.Calls);
            Assert.Equal(1, responder.Calls);
        }

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

        private static ConversationSessionFactory BuildGraph(
            IChatClient researcher,
            IChatClient responder,
            ScriptedModerationEvaluator? moderation)
        {
            RoutingChatClientFactory chatClients = new(researcher);
            _ = chatClients.Route("responder", responder);

            CompiledAgent compiled = ConfigurationCompiler.CompileAll(
                ConfigurationLoader.LoadYaml(GraphYaml),
                new AgentCompilationContext(chatClients)
                {
                    Moderation = moderation is null ? null : new PromptModerator(moderation),
                })["main"];

            return new ConversationSessionFactory(
                compiled,
                new GuardEvaluator(compiled.Configuration.Guards),
                hooks: BuiltInHooks.Create(new InMemoryAuditSink()));
        }

        private static CompiledAgent Compile(
            string yaml,
            IChatClient reply,
            out RoutingChatClientFactory chatClients,
            Func<ToolConfiguration, AITool?>? tools = null,
            ScriptedModerationEvaluator? moderation = null)
        {
            AgentCoreConfiguration document = ConfigurationLoader.LoadYaml(yaml);
            chatClients = new RoutingChatClientFactory(reply);

            return ConfigurationCompiler.CompileAll(
                document,
                new AgentCompilationContext(chatClients)
                {
                    Tools = TestToolRegistry.From(document, tools, TestContext.Current.CancellationToken),
                    Moderation = moderation is null ? null : new PromptModerator(moderation),
                })["main"];
        }

        private static ConversationSessionFactory Build(
            string yaml,
            IChatClient reply,
            IAuditSinkPort? sink = null,
            Func<ToolConfiguration, AITool?>? tools = null,
            ScriptedModerationEvaluator? moderation = null)
        {
            CompiledAgent compiled = Compile(yaml, reply, out _, tools, moderation);
            IAuditSinkPort rows = sink ?? new InMemoryAuditSink();

            return new ConversationSessionFactory(
                compiled,
                new GuardEvaluator(compiled.Configuration.Guards),
                hooks: BuiltInHooks.Create(rows));
        }
    }
}
