using AgentCore.Application.Audit.Memory;
using AgentCore.Application.Runtime;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Domain.Audit;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;
using static AgentCore.Application.Tests.Runtime.ConversationSessionCutTestSupport;

namespace AgentCore.Application.Tests.Runtime
{
    /// <summary>
    /// <see cref="ConversationSession.Recut"/>: a later account of what reached the user replaces the cut a turn
    /// already took, while that turn is the newest one started (owner ruling E3).
    /// </summary>
    public sealed class ConversationSessionRecutTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        [Fact]
        public async Task ARecutWhileTheCutTurnStillRuns_CommitsTheTurnWithTheRecutText()
        {
            // Arrange
            (ConversationSession session, RecordingConversationStore store, InMemoryAuditSink sink) = Create(TurnScriptChatClient.Text("Hello ", "there"));
            await using TurnRun run = await session.StartTurnAsync(new ChatMessage(ChatRole.User, "hi"), origin: null, Ct);

            // Act
            bool cut = false;
            bool recut = false;
            await foreach (ChatResponseUpdate update in run.Updates)
            {
                if (!cut && update.Text.Length > 0)
                {
                    cut = session.Cut(run.TurnIndex, new TurnCut("Hello ", Played: null));
                    recut = session.Recut(run.TurnIndex, new TurnCut("Hel", TimeSpan.FromMilliseconds(300)));
                }
            }

            await session.FlushTranscriptAsync();

            // Assert
            Assert.True(cut);
            Assert.True(recut);
            Assert.Equal(["hi", "Hel"], Texts(session.Transcript));
            Assert.Equal(TimeSpan.FromMilliseconds(300), session.LastTurn!.Cut);
            Assert.Equal(1, store.Appends);
            Assert.Empty(store.Rewrites);
            AuditEvent interrupted = Assert.Single(sink.EventsOf(session.ConversationId), item => item.Kind == AuditEventKind.ReplyInterrupted);
            Assert.Equal(AuditHash.OfText("Hel").Value, interrupted.Payload[AuditPayloadKeys.UtteranceUntilInterruptSha256]);
        }

        // TurnCutSlot remarks: a recut between the seal and the commit waits for the commit, which rewrites the reply
        // the cut shaped. The store holds the append, so the turn stays sealed and uncommitted meanwhile.
        [Fact]
        public async Task ARecutWhileTheCutTurnsAppendIsInFlight_RewritesTheReplyOnceItLands()
        {
            // Arrange
            ParkingConversationStore store = new();
            InMemoryAuditSink sink = new();
            ConversationSession session = Create(TurnScriptChatClient.Text("Hello ", "there"), store, sink);
            TurnRun run = await session.StartTurnAsync(new ChatMessage(ChatRole.User, "hi"), origin: null, Ct);
            TaskCompletionSource cutMade = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task reading = Task.Run(
                async () =>
                {
                    await using (run)
                    {
                        await foreach (ChatResponseUpdate update in run.Updates)
                        {
                            if (!cutMade.Task.IsCompleted && update.Text.Length > 0)
                            {
                                _ = session.Cut(run.TurnIndex, new TurnCut("Hello ", Played: null));
                                _ = cutMade.TrySetResult();
                            }
                        }
                    }
                },
                Ct);
            await cutMade.Task.WaitAsync(Ct);
            await store.Parked.WaitAsync(Ct);

            // Act
            bool recut = session.Recut(run.TurnIndex, new TurnCut("Hel", TimeSpan.FromMilliseconds(300)));
            store.Release();
            await reading.WaitAsync(Ct);
            await session.FlushTranscriptAsync();

            // Assert
            Assert.True(recut);
            Assert.Equal(["hi", "Hel"], Texts(session.Transcript));
            Assert.Equal(["hi", "Hel"], Texts((await store.ReadForSessionAsync(session.ConversationId, Ct)).Select(row => row.Content)));
            Assert.Equal(TimeSpan.FromMilliseconds(300), session.LastTurn!.Cut);
            Assert.Equal(
                [AuditHash.OfText("Hello").Value, AuditHash.OfText("Hel").Value],
                sink.EventsOf(session.ConversationId)
                    .Where(item => item.Kind == AuditEventKind.ReplyInterrupted)
                    .Select(item => item.Payload[AuditPayloadKeys.UtteranceUntilInterruptSha256]));
        }

        [Fact]
        public async Task ARecutOfACommittedCutTurn_RewritesTheReply_AndAmendsTurnCompletedAgain()
        {
            // Arrange
            (ConversationSession session, RecordingConversationStore store, InMemoryAuditSink sink) = Create(TurnScriptChatClient.Text("Hello ", "there"));
            _ = await session.RunTurnAsync("hi", Ct);
            _ = session.Cut(0, new TurnCut("Hello", TimeSpan.FromMilliseconds(100)));

            // Act
            bool recut = session.Recut(0, new TurnCut("Hello there", TimeSpan.FromMilliseconds(900)));
            bool cutAgain = session.Cut(0, new TurnCut("Hel", TimeSpan.FromMilliseconds(50)));
            await session.FlushTranscriptAsync();

            // Assert
            Assert.True(recut);
            Assert.False(cutAgain);
            Assert.Equal(["hi", "Hello there"], Texts(session.Transcript));
            Assert.Equal("Hello there", session.LastTurn!.ReplyText);
            Assert.Equal(TimeSpan.FromMilliseconds(900), session.LastTurn.Cut);
            Assert.Equal(1, store.Appends);
            Assert.Equal(2, store.Rewrites.Count);
            IReadOnlyList<AuditEvent> events = sink.EventsOf(session.ConversationId);
            AuditEvent completed = Assert.Single(events, item => item.Kind == AuditEventKind.TurnCompleted);
            List<AuditEvent> amendments = [.. events.Where(item => item.Kind == AuditEventKind.ReplyInterrupted)];
            Assert.Equal(2, amendments.Count);
            Assert.All(amendments, amendment => Assert.Equal(completed.EventId, amendment.AmendsEventId));
            Assert.Equal(AuditHash.OfText("Hello there").Value, amendments[^1].Payload[AuditPayloadKeys.UtteranceUntilInterruptSha256]);
            Assert.Equal("900", amendments[^1].Payload[AuditPayloadKeys.DurationUntilInterruptMs]);
        }

        [Fact]
        public async Task ARecutOfATurnThatTookNoCut_ReportsFalseAndChangesNothing()
        {
            // Arrange
            (ConversationSession session, RecordingConversationStore store, InMemoryAuditSink sink) = Create(TurnScriptChatClient.Text("Hello ", "there"));
            _ = await session.RunTurnAsync("hi", Ct);

            // Act
            bool recut = session.Recut(0, new TurnCut("Hello", TimeSpan.FromMilliseconds(100)));
            await session.FlushTranscriptAsync();

            // Assert
            Assert.False(recut);
            Assert.Equal(["hi", "Hello there"], Texts(session.Transcript));
            Assert.Empty(store.Rewrites);
            Assert.DoesNotContain(sink.EventsOf(session.ConversationId), item => item.Kind == AuditEventKind.ReplyInterrupted);
        }

        // Owner ruling E3: once a later turn has started, an earlier turn's reply never changes.
        [Fact]
        public async Task ARecutOfACutTurnOnceTheNextTurnStarted_ReportsFalseAndChangesNothing()
        {
            // Arrange
            (ConversationSession session, RecordingConversationStore store, _) = Create(TurnScriptChatClient.Sequence(["Hello there"], ["next"]));
            _ = await session.RunTurnAsync("hi", Ct);
            _ = session.Cut(0, new TurnCut("Hello", null));
            await using TurnRun next = await session.StartTurnAsync(new ChatMessage(ChatRole.User, "and?"), origin: null, Ct);

            // Act
            bool recut = session.Recut(0, new TurnCut("Hello there", TimeSpan.FromMilliseconds(900)));
            await ReadToEndAsync(next, Ct);
            await session.FlushTranscriptAsync();

            // Assert
            Assert.False(recut);
            Assert.Equal(["hi", "Hello", "and?", "next"], Texts(session.Transcript));
            _ = Assert.Single(store.Rewrites);
        }
    }
}
