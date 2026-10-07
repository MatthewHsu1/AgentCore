using System.Diagnostics;
using System.Diagnostics.Metrics;
using AgentCore.Application.Audit.Memory;
using AgentCore.Application.Conversation;
using AgentCore.Application.Diagnostics;
using AgentCore.Application.Tests.Audit;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Domain.Audit;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Application.Runtime.Session;
using AgentCore.Application.Runtime.Turn.Lifecycle;
using static AgentCore.Application.Tests.Runtime.ConversationSessionCutTestSupport;

namespace AgentCore.Application.Tests.Runtime
{
    /// <summary>
    /// The <see cref="TurnRun"/> a started turn hands back: how its disposal and its one read hold and free the
    /// conversation.
    /// </summary>
    public sealed class ConversationSessionTurnRunTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        // TurnRun.DisposeAsync is safe to call more than once: a second disposal of a run nobody read frees nothing,
        // so the turn that started after the first disposal still holds the conversation.
        [Fact]
        public async Task DisposingAnUnreadRunTwice_LeavesTheTurnStartedSinceHoldingTheConversation()
        {
            // Arrange
            (ConversationSession session, _, _) = Create(TurnScriptChatClient.Text("reply"));
            TurnRun unread = await session.StartTurnAsync(new ChatMessage(ChatRole.User, "one"), origin: null, Ct);
            await unread.DisposeAsync();
            await using TurnRun running = await session.StartTurnAsync(new ChatMessage(ChatRole.User, "two"), origin: null, Ct);

            // Act
            await unread.DisposeAsync();

            // Assert: a third turn still waits for "two", so it gives up rather than start.
            using CancellationTokenSource giveUp = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            giveUp.CancelAfter(TimeSpan.FromMilliseconds(200));
            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => session.StartTurnAsync(new ChatMessage(ChatRole.User, "three"), origin: null, giveUp.Token));
        }

        // A run disposed unread commits nothing, and its reply can no longer be read.
        [Fact]
        public async Task ARunDisposedUnread_RefusesToBeRead_AndNeverCallsTheModel()
        {
            // Arrange
            TurnScriptChatClient model = TurnScriptChatClient.Text("reply");
            (ConversationSession session, RecordingConversationStore store, _) = Create(model);
            TurnRun run = await session.StartTurnAsync(new ChatMessage(ChatRole.User, "one"), origin: null, Ct);
            await run.DisposeAsync();

            // Act
            _ = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await foreach (ChatResponseUpdate _ in run.Updates)
                {
                }
            });
            await session.FlushTranscriptAsync();

            // Assert
            Assert.Equal(0, model.Calls);
            Assert.Equal(0, store.Appends);
        }

        // BeginTurn opens the span before the caller asks for the reply, so a run disposed unread still closes it,
        // with the outcome and the duration a stopped turn gets.
        [Fact]
        public async Task ARunDisposedUnread_StillEndsItsSpan_WithAnInterruptedOutcomeAndADuration()
        {
            // Arrange
            List<Activity> spans = [];
            using ActivityListener listener = ListenToAgentCoreTelemetry(spans);

            List<double> durations = [];
            using MeterListener meter = new()
            {
                InstrumentPublished = (instrument, active) =>
                {
                    if (string.Equals(instrument.Meter.Name, AgentCoreTelemetry.MeterName, StringComparison.Ordinal)
                        && string.Equals(instrument.Name, "agentcore.turn.duration", StringComparison.Ordinal))
                    {
                        active.EnableMeasurementEvents(instrument);
                    }
                },
            };
            meter.SetMeasurementEventCallback<double>((_, measurement, _, _) =>
            {
                lock (durations)
                {
                    durations.Add(measurement);
                }
            });
            meter.Start();

            (ConversationSession session, _, _) = Create(TurnScriptChatClient.Text("reply"));
            TurnRun run = await session.StartTurnAsync(new ChatMessage(ChatRole.User, "one"), origin: null, Ct);

            // Act
            await run.DisposeAsync();

            // Assert
            List<Activity> snapshot;
            lock (spans)
            {
                snapshot = [.. spans];
            }

            Activity span = Assert.Single(snapshot, item => string.Equals(
                item.GetTagItem("gen_ai.conversation.id") as string, session.ConversationId, StringComparison.Ordinal));
            Assert.Equal("interrupted", span.GetTagItem("agentcore.turn.outcome"));

            lock (durations)
            {
                Assert.NotEmpty(durations);
            }
        }

        // A turn that is refused or dropped for any reason leaves a log line and an audit event.
        [Fact]
        public async Task ARunDisposedUnread_LeavesATurnRefusedThatSaysItWasDropped()
        {
            // Arrange
            (ConversationSession session, _, InMemoryAuditSink sink) = Create(TurnScriptChatClient.Text("reply"));
            TurnRun run = await session.StartTurnAsync(new ChatMessage(ChatRole.User, "one"), origin: null, Ct);

            // Act
            await run.DisposeAsync();

            // Assert
            AuditEvent refused = Assert.Single(await session.RowsAsync(sink), item => item.Kind == AuditEventKind.TurnRefused);
            Assert.Equal((0, "dropped"), (refused.TurnIndex, refused.Payload[AuditPayloadKeys.RefusedReason]));
        }

        [Fact]
        public async Task ATurnStartedWhileAnotherRunsPastTheWaitLimit_IsRefusedAsBusy()
        {
            // Arrange
            FakeTimeProvider time = new(DateTimeOffset.UnixEpoch);
            InMemoryAuditSink sink = new();
            ConversationSession session = Create(TurnScriptChatClient.Text("reply"), new RecordingConversationStore(), sink, time);
            await using TurnRun running = await session.StartTurnAsync(new ChatMessage(ChatRole.User, "one"), origin: null, Ct);

            // Act
            Task<TurnRun> starting = session.StartTurnAsync(new ChatMessage(ChatRole.User, "two"), origin: null, Ct);
            _ = await Task.WhenAny(time.WaitForTimersAsync(time.GetUtcNow() + ConversationBusyMark.WaitLimit, 1), starting);
            time.Advance(ConversationBusyMark.WaitLimit);

            // Assert
            _ = await Assert.ThrowsAsync<ConversationTurnConflictException>(() => starting);
            AuditEvent refused = Assert.Single(await session.RowsAsync(sink), item => item.Kind == AuditEventKind.TurnRefused);
            Assert.Equal((null, "busy"), (refused.TurnIndex, refused.Payload[AuditPayloadKeys.RefusedReason]));
        }

        /// <summary>Subscribes to the one activity source of this library, and no other.</summary>
        private static ActivityListener ListenToAgentCoreTelemetry(List<Activity> spans)
        {
            ActivityListener listener = new()
            {
                ShouldListenTo = source =>
                    string.Equals(source.Name, AgentCoreTelemetry.ActivitySourceName, StringComparison.Ordinal),
                Sample = (ref _) => ActivitySamplingResult.AllData,
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
    }
}
