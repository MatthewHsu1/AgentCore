using AgentCore.Application.Audit.Memory;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Conversation.Memory;
using AgentCore.Application.Hooks;
using AgentCore.Application.Ports;
using AgentCore.Application.Tests.Audit;
using AgentCore.Application.Transcript;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Domain;
using AgentCore.Domain.Audit;
using AgentCore.TestSupport;
using Microsoft.Extensions.Logging;
using Xunit;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.Application.Tests.Runtime
{
    /// <summary>
    /// The busy mark a turn puts on its conversation in the store (modelled on LangGraph's thread
    /// <c>busy</c> status and its <c>enqueue</c> strategy): a turn of another session waits for it, then reads the
    /// conversation afresh.
    /// </summary>
    public sealed class ConversationBusyMarkTests
    {
        private const string ConversationId = "c-busy";

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        [Fact(Timeout = 30_000)]
        public async Task ATurnOfAnotherSessionStillSaving_HoldsTheNextTurn_WhichThenTakesTheTurnAfterIt()
        {
            // Arrange: the first session's words are still on their way to the store when the second one starts.
            ParkingConversationStore parking = new();
            MarkWatchingStore store = new(parking);
            RequestRecordingChatClient model = new("first reply", "second reply");
            ConversationSessionFactory factory = Build(model, store, TimeProvider.System, new InMemoryAuditSink(), logger: null);
            ConversationSession first = factory.Create(ConversationId);
            ConversationSession second = factory.Create(ConversationId);

            Task<TurnResult> running = first.RunTurnAsync("first", Ct);
            await parking.Parked.WaitAsync(Ct);

            // Act
            Task<TurnResult> next = second.RunTurnAsync("second", Ct);
            _ = await Task.WhenAny(store.MarkRefused, next);
            parking.Release();
            TurnResult firstResult = await running;
            TurnResult nextResult = await next;

            // Assert
            Assert.Equal((0, 1), (firstResult.TurnIndex, nextResult.TurnIndex));
            IReadOnlyList<ConversationMessage> rows = await store.ReadAllAsync(ConversationId, Ct);
            Assert.Equal(
                [(0, "first"), (0, "first reply"), (1, "second"), (1, "second reply")],
                rows.Select(row => (row.TurnIndex, row.Content.Text)));
        }

        [Fact(Timeout = 30_000)]
        public async Task AMarkLeftByAHostThatCrashed_HoldsTheTurnUntilItsLeaseLapses_ThenTheTurnRuns()
        {
            // Arrange: a host put the mark and died, so nothing renews or clears it.
            FakeTimeProvider time = new(DateTimeOffset.UnixEpoch);
            InMemoryConversationStore store = new(time);
            RequestRecordingChatClient model = new("hello");
            ConversationSession session = Build(model, store, time, new InMemoryAuditSink(), logger: null).Create(ConversationId);
            _ = await store.TryMarkBusyAsync(ConversationId, "crashed-host", ConversationBusyMark.Lease, Ct);

            // Act
            Task<TurnResult> turn = session.RunTurnAsync("hi", Ct);
            _ = await Task.WhenAny(time.WaitForTimersAsync(time.GetUtcNow() + ConversationBusyMark.FirstPoll, 1), turn);
            bool heldWhileLive = !turn.IsCompleted && model.Requests.Count == 0;
            time.Advance(ConversationBusyMark.Lease);
            TurnResult result = await turn;

            // Assert
            Assert.True(heldWhileLive);
            Assert.Equal((0, "hello", null), (result.TurnIndex, result.ReplyText, result.Failure));
        }

        [Fact(Timeout = 30_000)]
        public async Task ACallerThatLeavesWhileTheTurnWaits_DropsTheTurn_WithAWarningAndATurnRefused()
        {
            // Arrange
            FakeTimeProvider time = new(DateTimeOffset.UnixEpoch);
            InMemoryConversationStore store = new(time);
            RequestRecordingChatClient model = new("hello");
            InMemoryAuditSink sink = new();
            using RecordingLoggerFactory logs = new();
            ConversationSession session = Build(model, store, time, sink, logs.CreateLogger("session")).Create(ConversationId);
            _ = await store.TryMarkBusyAsync(ConversationId, "other-host", TimeSpan.FromHours(1), Ct);
            using CancellationTokenSource caller = CancellationTokenSource.CreateLinkedTokenSource(Ct);

            // Act
            Task<TurnResult> turn = session.RunTurnAsync("hi", caller.Token);
            _ = await Task.WhenAny(time.WaitForTimersAsync(time.GetUtcNow() + ConversationBusyMark.FirstPoll, 1), turn);
            await caller.CancelAsync();
            Exception? dropped = await Record.ExceptionAsync(() => turn);

            // Assert
            _ = Assert.IsType<OperationCanceledException>(dropped, exactMatch: false);
            Assert.Empty(model.Requests);
            AuditEvent refused = Assert.Single(await session.RowsAsync(sink), item => item.Kind == AuditEventKind.TurnRefused);
            Assert.Equal(("gone", null), (refused.Payload[AuditPayloadKeys.RefusedReason], refused.TurnIndex));
            CapturedLine line = Assert.Single(logs.Of(40));
            Assert.Equal((LogLevel.Warning, "gone"), (line.Level, line.Field<string>("Reason")));
        }

        // A refusal before the store opened starts the conversation; the turn that later opens it starts nothing more.
        [Fact(Timeout = 30_000)]
        public async Task ATurnAfterARefusedOneStartsNoSecondConversation()
        {
            // Arrange
            FakeTimeProvider time = new(DateTimeOffset.UnixEpoch);
            InMemoryConversationStore store = new(time);
            RequestRecordingChatClient model = new("hello");
            InMemoryAuditSink sink = new();
            ConversationSession session = Build(model, store, time, sink, logger: null).Create(ConversationId);
            _ = await store.TryMarkBusyAsync(ConversationId, "other-host", TimeSpan.FromHours(1), Ct);
            using CancellationTokenSource caller = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            Task<TurnResult> dropped = session.RunTurnAsync("hi", caller.Token);
            _ = await Task.WhenAny(time.WaitForTimersAsync(time.GetUtcNow() + ConversationBusyMark.FirstPoll, 1), dropped);
            await caller.CancelAsync();
            _ = await Record.ExceptionAsync(() => dropped);
            await store.ClearBusyAsync(ConversationId, "other-host", Ct);

            // Act
            _ = await session.RunTurnAsync("hi again", Ct);

            // Assert
            Assert.Equal(
                [AuditEventKind.ConversationStarted, AuditEventKind.TurnRefused, AuditEventKind.TurnCompleted],
                (await session.RowsAsync(sink)).Select(row => row.Kind));
        }

        private static ConversationSessionFactory Build(
            RequestRecordingChatClient model, IConversationStore store, TimeProvider time, InMemoryAuditSink sink, ILogger? logger)
        {
            CompiledAgent compiled = ConfigurationCompiler.CompileAll(
                ConfigurationLoader.LoadYaml(ConversationSessionTestSupport.OneAgentYaml),
                new AgentCompilationContext(new FakeChatClientFactory(model)) { ConversationStore = store })["main"];

            return new ConversationSessionFactory(
                compiled,
                new GuardEvaluator(compiled.Configuration.Guards),
                extractor: null,
                timeProvider: time,
                logger: logger,
                hooks: BuiltInHooks.Create(sink, logger));
        }

        /// <summary>Reports the first time a turn found the conversation marked by another session.</summary>
        private sealed class MarkWatchingStore(IConversationStore inner) : DelegatingConversationStore(inner)
        {
            private readonly TaskCompletionSource _refused = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task MarkRefused => _refused.Task;

            public override async ValueTask<bool> TryMarkBusyAsync(
                string conversationId, string holder, TimeSpan lease, CancellationToken cancellationToken = default)
            {
                bool marked = await base.TryMarkBusyAsync(conversationId, holder, lease, cancellationToken);
                if (!marked)
                {
                    _ = _refused.TrySetResult();
                }

                return marked;
            }
        }
    }
}
