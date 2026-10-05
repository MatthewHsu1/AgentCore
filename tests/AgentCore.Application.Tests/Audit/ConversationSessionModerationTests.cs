using AgentCore.TestSupport;
using AgentCore.Application.Audit.Memory;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Evaluation;
using AgentCore.Application.Hooks;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime.Turn;
using AgentCore.Application.Tests.Diagnostics;
using AgentCore.Application.Tests.Evaluation.Fakes;
using AgentCore.Application.Tests.Runtime;
using AgentCore.Domain.Audit;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Xunit;
using AgentCore.Domain;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.Application.Tests.Audit
{
    /// <summary>
    /// Moderation reads what the caller said before the model runs, and refuses a flagged turn.
    /// </summary>
    public sealed class ConversationSessionModerationTests
    {
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

        private const string ExtractingYaml =
            """
        apiVersion: agentcore/v1
        state:
          callerSaidGoodbye: { type: boolean, default: false, writer: extractor }
        extractor:
          model: { ref: fill }
          when: after_reply
        agents:
          defaults:
            model: { ref: reply }
          items:
            - { id: only, instructions: "I answer everything" }
        entries:
          main:
            agent: only
        """;

        // A flagged prompt.
        [Fact]
        public async Task AFlaggedPrompt_WritesTheFlagBeforeTheTurnEvent()
        {
            InMemoryAuditSink sink = new();
            ConversationSession session = Build(PlainYaml, new SequencedChatClient("never spoken"), sink: sink,
                moderation: ScriptedModerationEvaluator.Flagging("harassment")).Create("conversation-1");

            _ = await session.RunTurnAsync("...", TestContext.Current.CancellationToken);

            IReadOnlyList<AuditEvent> events = await session.RowsAsync(sink);
            AuditEvent flagged = Assert.Single(events, e => e.Kind == AuditEventKind.PromptFlagged);
            AuditEvent completed = Assert.Single(events, e => e.Kind == AuditEventKind.TurnCompleted);

            // The verdict precedes the model, so the flag precedes the turn event and amends nothing.
            List<AuditEvent> order = [.. events];
            Assert.True(order.IndexOf(flagged) < order.IndexOf(completed));
            Assert.Null(flagged.AmendsEventId);
            Assert.Equal(0, flagged.TurnIndex);
        }

        [Fact]
        public async Task AFlaggedPrompt_CarriesTheCategoriesInTheOrderTheEndpointReturnedThem()
        {
            InMemoryAuditSink sink = new();
            ConversationSession session = Build(PlainYaml, new SequencedChatClient("never spoken"), sink: sink,
                moderation: ScriptedModerationEvaluator.Flagging("violence", "harassment")).Create("conversation-1");

            _ = await session.RunTurnAsync("...", TestContext.Current.CancellationToken);

            AuditEvent flagged = Assert.Single(await session.RowsAsync(sink), e => e.Kind == AuditEventKind.PromptFlagged);
            Assert.Equal("violence,harassment", flagged.Payload[AuditPayloadKeys.ModerationCategories]);
        }

        [Fact]
        public async Task AFlaggedPrompt_NeverReachesTheModel()
        {
            SequencedChatClient model = new("never spoken");
            ConversationSession session = Build(PlainYaml, model, moderation: ScriptedModerationEvaluator.Flagging("hate"))
                .Create("conversation-1");

            _ = await session.RunTurnAsync("...", TestContext.Current.CancellationToken);

            // Nothing was generated, so nothing harmful was ever produced to be recorded.
            Assert.Equal(0, model.Calls);
        }

        [Fact]
        public async Task AFlaggedPrompt_SpeaksTheRefusalLineAndNotTheFallback()
        {
            ConversationSession session = Build(PlainYaml, new SequencedChatClient("never spoken"),
                moderation: ScriptedModerationEvaluator.Flagging("hate")).Create("conversation-1");

            TurnResult result = await session.RunTurnAsync("...", TestContext.Current.CancellationToken);

            Assert.Equal("I am sorry. I cannot help with that request.", result.ReplyText);
            Assert.NotEqual(AgentCoreConfiguration.DefaultFallbackReply, result.ReplyText);
        }

        [Fact]
        public async Task AFlaggedPrompt_IsNotAFailure()
        {
            ConversationSession session = Build(PlainYaml, new SequencedChatClient("never spoken"),
                moderation: ScriptedModerationEvaluator.Flagging("hate")).Create("conversation-1");

            TurnResult result = await session.RunTurnAsync("...", TestContext.Current.CancellationToken);

            // A refusal is not a failure. Nothing broke, and the model was never asked.
            Assert.Null(result.Failure);
        }

        [Fact]
        public async Task AFlaggedPrompt_RecordsTheRefusalAsTheTextTheCallerHeard()
        {
            InMemoryAuditSink sink = new();
            ConversationSession session = Build(PlainYaml, new SequencedChatClient("never spoken"), sink: sink,
                moderation: ScriptedModerationEvaluator.Flagging("hate")).Create("conversation-1");

            _ = await session.RunTurnAsync("...", TestContext.Current.CancellationToken);

            AuditEvent completed = Assert.Single(await session.RowsAsync(sink), e => e.Kind == AuditEventKind.TurnCompleted);
            Assert.Equal(
                AuditHash.OfText("I am sorry. I cannot help with that request.").Value,
                completed.Payload[AuditPayloadKeys.ReplyTextSha256]);
        }

        [Fact]
        public async Task AFlaggedPrompt_RunsNoExtractor()
        {
            // The extractor's only input is the words moderation flagged. A slot filled from them would
            // carry the flagged content into the state document and into every later prompt.
            SequencedChatClient fill = new(/*lang=json,strict*/ """{ "callerSaidGoodbye": true }""");
            ConversationSession session = Build(ExtractingYaml, new SequencedChatClient("never spoken"), fill: fill,
                moderation: ScriptedModerationEvaluator.Flagging("hate")).Create("conversation-1");

            _ = await session.RunTurnAsync("...", TestContext.Current.CancellationToken);

            Assert.Equal(0, fill.Calls);
        }

        [Fact]
        public async Task AFlaggedPrompt_IsLoggedOnceAndNeverCarriesTheWordsThatWereFlagged()
        {
            RecordingLogger logger = new();
            ConversationSession session = Build(PlainYaml, new SequencedChatClient("never spoken"), logger: logger,
                moderation: ScriptedModerationEvaluator.Flagging("harassment")).Create("conversation-1");

            _ = await session.RunTurnAsync("the words that were flagged", TestContext.Current.CancellationToken);
            await session.FlushNoticesAsync();

            LogLine entry = Assert.Single(logger.Of(6));
            Assert.Equal(LogLevel.Warning, entry.Level);
            Assert.Contains("harassment", entry.Message, StringComparison.Ordinal);

            // The flagged words are the content. A log store is no place for the words,
            // so the categories travel and the words do not.
            Assert.DoesNotContain("the words that were flagged", entry.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task TheEventsOfAModeratedConversation_FormAChainThatVerifies()
        {
            InMemoryAuditSink sink = new();
            ConversationSession session = Build(PlainYaml, new SequencedChatClient("never spoken"), sink: sink,
                moderation: ScriptedModerationEvaluator.Flagging("harassment")).Create("conversation-1");

            _ = await session.RunTurnAsync("...", TestContext.Current.CancellationToken);

            IReadOnlyList<AuditEvent> events = await session.RowsAsync(sink);
            Assert.Equal(
                [AuditEventKind.ConversationStarted, AuditEventKind.PromptFlagged, AuditEventKind.TurnCompleted],
                events.Select(e => e.Kind).ToArray());
            Assert.All(events, AuditEventVocabulary.Validate);
        }

        // A clean prompt, and the fail-open rule.
        [Fact]
        public async Task ACleanPrompt_ReachesTheModelAndWritesNoFlag()
        {
            InMemoryAuditSink sink = new();
            SequencedChatClient model = new("the ordinary reply");
            ConversationSession session = Build(PlainYaml, model, sink: sink, moderation: ScriptedModerationEvaluator.Clean())
                .Create("conversation-1");

            TurnResult result = await session.RunTurnAsync("where is my order", TestContext.Current.CancellationToken);

            Assert.Equal(1, model.Calls);
            Assert.Equal("the ordinary reply", result.ReplyText);

            // The vocabulary holds no reply.cleared. A turn.completed that no flag precedes is the record.
            Assert.DoesNotContain(await session.RowsAsync(sink), e => e.Kind == AuditEventKind.PromptFlagged);
        }

        [Fact]
        public async Task TheModeratedText_IsWhatTheCallerSaid()
        {
            ScriptedModerationEvaluator endpoint = ScriptedModerationEvaluator.Clean();
            ConversationSession session = Build(PlainYaml, new SequencedChatClient("hello"), moderation: endpoint).Create("conversation-1");

            _ = await session.RunTurnAsync("how do I reset the console", TestContext.Current.CancellationToken);

            Assert.Equal(["how do I reset the console"], endpoint.Moderated);
        }

        [Fact]
        public async Task AnEndpointThatDidNotAnswer_LetsTheTurnThrough()
        {
            // Fail open. A vendor outage must not refuse every caller on a support line.
            InMemoryAuditSink sink = new();
            SequencedChatClient model = new("the ordinary reply");
            ConversationSession session = Build(PlainYaml, model, sink: sink, moderation: ScriptedModerationEvaluator.Unanswered())
                .Create("conversation-1");

            TurnResult result = await session.RunTurnAsync("...", TestContext.Current.CancellationToken);

            Assert.Equal(1, model.Calls);
            Assert.Equal("the ordinary reply", result.ReplyText);
            Assert.DoesNotContain(await session.RowsAsync(sink), e => e.Kind == AuditEventKind.PromptFlagged);
        }

        [Fact]
        public async Task AnEndpointThatThrows_LetsTheTurnThroughAndLogsOnce()
        {
            RecordingLogger logger = new();
            SequencedChatClient model = new("the ordinary reply");
            ConversationSession session = Build(PlainYaml, model, logger: logger,
                moderation: ScriptedModerationEvaluator.Throwing(new InvalidOperationException("boom")))
                .Create("conversation-1");

            TurnResult result = await session.RunTurnAsync("...", TestContext.Current.CancellationToken);
            await session.FlushNoticesAsync();

            Assert.Equal("the ordinary reply", result.ReplyText);
            Assert.Equal(1, model.Calls);

            LogLine entry = Assert.Single(logger.Of(7));
            Assert.Equal(LogLevel.Warning, entry.Level);
        }

        [Fact]
        public async Task AnEndpointThatFails_WritesNoFlagAndNeverTheWordUnknown()
        {
            InMemoryAuditSink sink = new();
            ConversationSession session = Build(PlainYaml, new SequencedChatClient("the ordinary reply"), sink: sink,
                moderation: ScriptedModerationEvaluator.Throwing(new InvalidOperationException("boom")))
                .Create("conversation-1");

            _ = await session.RunTurnAsync("...", TestContext.Current.CancellationToken);

            // A missing fact is an absent event, never an event carrying "unknown". The chain rule would
            // refuse a prompt.flagged with no category anyway.
            Assert.DoesNotContain(await session.RowsAsync(sink), e => e.Kind == AuditEventKind.PromptFlagged);
        }

        [Fact]
        public async Task ASessionWithNoModerator_RunsATurnAndWritesNoFlag()
        {
            InMemoryAuditSink sink = new();
            SequencedChatClient model = new("the ordinary reply");
            ConversationSession session = Build(PlainYaml, model, sink: sink).Create("conversation-1");

            TurnResult result = await session.RunTurnAsync("...", TestContext.Current.CancellationToken);

            Assert.Equal("the ordinary reply", result.ReplyText);
            Assert.Equal(2, (await session.RowsAsync(sink)).Count);
        }

        // The streaming path takes the same decision.
        [Fact]
        public async Task AFlaggedPrompt_StreamsTheRefusalAndOpensNoModelStream()
        {
            InMemoryAuditSink sink = new();
            SequencedChatClient model = new("never spoken");
            ConversationSession session = Build(PlainYaml, model, sink: sink,
                moderation: ScriptedModerationEvaluator.Flagging("harassment")).Create("conversation-1");

            List<string> spoken = [];
            await foreach (ChatResponseUpdate update in session.RunTurnStreamingAsync("...", TestContext.Current.CancellationToken))
            {
                // The trailing update is the turn's committed ids, not text.
                if (update.Contents.OfType<TurnCommittedContent>().Any())
                {
                    continue;
                }

                spoken.Add(update.Text);
            }

            Assert.Equal(["I am sorry. I cannot help with that request."], spoken);
            Assert.Equal(0, model.Calls);
            Assert.Contains(await session.RowsAsync(sink), e => e.Kind == AuditEventKind.PromptFlagged);
        }

        [Fact]
        public async Task ACleanPromptOnTheStreamingPath_ReachesTheModel()
        {
            SequencedChatClient model = new("the ordinary reply");
            ConversationSession session = Build(PlainYaml, model, moderation: ScriptedModerationEvaluator.Clean()).Create("conversation-1");

            await foreach (ChatResponseUpdate _ in session.RunTurnStreamingAsync("hello", TestContext.Current.CancellationToken))
            {
                // Drain it.
            }

            Assert.Equal(1, model.Calls);
        }

        // Helpers.
        private static ConversationSessionFactory Build(
            string yaml,
            IChatClient reply,
            IChatClient? fill = null,
            IAuditSinkPort? sink = null,
            ILogger? logger = null,
            ScriptedModerationEvaluator? moderation = null)
        {
            // The audit hook takes a required sink, because the composition root resolves providers.audit for
            // every host and falls back to the in-process memory kind. An optional parameter has to be a
            // compile-time constant, so the default is spelled here instead — a fact that does not care where
            // its events land gets a fresh in-memory sink.
            IAuditSinkPort auditSink = sink ?? new InMemoryAuditSink();

            AgentCoreConfiguration document = ConfigurationLoader.LoadYaml(yaml);
            RoutingChatClientFactory chatClients = new(reply);
            if (fill is not null)
            {
                _ = chatClients.Route("fill", fill);
            }

            // Moderation sits in the chat pipeline of every compiled agent, so the moderator is
            // bound at compile time and not on the session factory.
            CompiledAgent compiled = ConfigurationCompiler.CompileAll(
                document,
                new AgentCompilationContext(chatClients)
                {
                    Moderation = moderation is null ? null : new PromptModerator(moderation),
                })["main"];

            return new ConversationSessionFactory(
                compiled,
                new GuardEvaluator(compiled.Configuration.Guards),
                ConversationSessionFactory.CreateExtractor(compiled, chatClients),
                timeProvider: null,
                logger,
                hooks: BuiltInHooks.Create(auditSink, logger));
        }
    }
}
