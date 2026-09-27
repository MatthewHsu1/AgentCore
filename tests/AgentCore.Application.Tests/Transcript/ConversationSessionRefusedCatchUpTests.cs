using AgentCore.Application.Audit.Memory;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Conversation;
using AgentCore.Application.Conversation.Memory;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Tests.Runtime;
using AgentCore.Domain;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentCore.Application.Tests.Transcript
{
    /// <summary>
    /// A long-lived session whose turn the store refused, because another session saved that turn first: its next
    /// turn runs on what the store holds, not on the words and state of the turn it lost.
    /// </summary>
    public sealed class ConversationSessionRefusedCatchUpTests
    {
        private const string ConversationId = "c-refused";

        /// <summary>The event id of <c>Log.TurnRunFaulted</c>.</summary>
        private const int TurnRunFaultedEvent = 28;

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        [Fact(Timeout = 30_000)]
        public async Task AfterARefusedTurn_TheNextTurnRunsOnTheStoredWordsAndState()
        {
            // Arrange: the busy mark fails open, so both sessions run turn 0 at once, and B saves it first.
            HeldFirstTurnChatClient reply = new("I am Alice");
            InMemoryConversationStore inner = new();
            ConversationSessionFactory factory = Build(reply, new UnmarkableStore(inner));
            ConversationSession a = factory.Create(ConversationId);
            ConversationSession b = factory.Create(ConversationId);

            Task<TurnResult> alice = a.RunTurnAsync("I am Alice", Ct);
            await reply.Held.Task.WaitAsync(Ct);
            _ = await b.RunTurnAsync("I am Bob", Ct);
            reply.Release();
            Exception? refused = await Record.ExceptionAsync(() => alice);

            // Act
            TurnResult next = await a.RunTurnAsync("what is my name", Ct);
            await a.FlushTranscriptAsync();

            // Assert
            _ = Assert.IsType<ConversationTurnConflictException>(refused);
            Assert.Equal(1, next.TurnIndex);
            Assert.Equal(
                ["I am Bob", "noted", "what is my name"],
                reply.LastRequest.Where(message => message.Text.Length > 0 && message.Role != ChatRole.System)
                    .Select(message => message.Text));
            ConversationRecord? stored = await inner.GetAsync(ConversationId, Ct);
            Assert.Equal("\"Bob\"", stored?.State?.Slots["callerName"]?.ToJsonString());
            Assert.Equal(2, stored?.State?.NextTurnIndex);
        }

        [Fact(Timeout = 30_000)]
        public async Task ARunFaultOnATurnTheStoreRefused_IsLoggedOnce()
        {
            // Arrange
            HeldFirstTurnChatClient reply = new("I am Alice") { FaultsHeldRequest = true };
            using RecordingLoggerFactory logs = new();
            ConversationSessionFactory factory = Build(reply, new UnmarkableStore(new InMemoryConversationStore()), logs.CreateLogger("session"));
            ConversationSession a = factory.Create(ConversationId);
            ConversationSession b = factory.Create(ConversationId);

            Task<TurnResult> alice = a.RunTurnAsync("I am Alice", Ct);
            await reply.Held.Task.WaitAsync(Ct);
            _ = await b.RunTurnAsync("I am Bob", Ct);

            // Act
            reply.Release();
            Exception? refused = await Record.ExceptionAsync(() => alice);

            // Assert
            _ = Assert.IsType<ConversationTurnConflictException>(refused);
            CapturedLine line = Assert.Single(logs.Of(TurnRunFaultedEvent));
            Assert.Equal(LogLevel.Error, line.Level);
            _ = Assert.IsType<HttpRequestException>(line.Exception);
        }

        private static ConversationSessionFactory Build(HeldFirstTurnChatClient reply, IConversationStore store, ILogger? logger = null)
        {
            RoutingChatClientFactory clients = new(reply);
            _ = clients.Route("fill", new CallerNameFillChatClient());
            CompiledAgent compiled = ConfigurationCompiler.CompileAll(
                ConfigurationLoader.LoadYaml(InterruptionSessions.ExtractorYaml),
                new AgentCompilationContext(clients) { ConversationStore = store })["main"];

            return new ConversationSessionFactory(
                compiled,
                new GuardEvaluator(compiled.Configuration.Guards),
                ConversationSessionFactory.CreateExtractor(compiled, clients),
                logger: logger,
                observers: ConversationObservers.Standard(new InMemoryAuditSink(), logger));
        }

        /// <summary>A store whose busy table cannot be reached, so the busy mark fails open.</summary>
        private sealed class UnmarkableStore(IConversationStore inner) : DelegatingConversationStore(inner)
        {
            public override ValueTask<bool> TryMarkBusyAsync(
                string conversationId, string holder, TimeSpan lease, CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("the busy table is unreachable");
            }
        }
    }
}
