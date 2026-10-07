using AgentCore.Application.Audit.Memory;
using AgentCore.Application.Runtime.Turn;
using AgentCore.Application.Tests.Audit;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Domain;
using AgentCore.Domain.Audit;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Application.Runtime.Cut;
using AgentCore.Application.Runtime.Session;
using AgentCore.Application.Runtime.Turn.Lifecycle;
using static AgentCore.Application.Tests.Runtime.ConversationSessionCutTestSupport;

namespace AgentCore.Application.Tests.Runtime
{
    /// <summary>
    /// <see cref="ConversationSession.Cut"/> and the Cut rule over a whole conversation.
    /// </summary>
    public sealed class ConversationSessionCutTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        // A host cancel mid-reply keeps the user
        // message and everything yielded, and the host still gets its cancellation.
        [Fact]
        public async Task HostCancelMidReply_KeepsTheUserMessageAndTheYieldedText()
        {
            // Arrange
            (ConversationSession session, RecordingConversationStore store, _) = Create(TurnScriptChatClient.Text("Hel", "lo ", "there"));
            using CancellationTokenSource stop = new();

            // Act
            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await foreach (ChatResponseUpdate _ in session.RunTurnStreamingAsync("hi", stop.Token))
                {
                    await stop.CancelAsync();
                }
            });
            await session.FlushTranscriptAsync();

            // Assert
            Assert.Equal(["hi", "Hel"], Texts(session.Transcript));
            Assert.Equal(1, store.Appends);
            Assert.NotNull(session.LastTurn);
            Assert.Equal("Hel", session.LastTurn.ReplyText);
        }

        // A host that stops reading still gets the turn committed, and the
        // conversation takes the next turn.
        [Fact]
        public async Task HostAbandonsTheStream_KeepsTheUserAndTheYieldedText_AndTheNextTurnRuns()
        {
            // Arrange
            (ConversationSession session, RecordingConversationStore store, _) = Create(TurnScriptChatClient.Sequence(["Hel", "lo ", "there"], ["next"]));

            // Act
            await foreach (ChatResponseUpdate update in session.RunTurnStreamingAsync("hi", Ct))
            {
                if (update.Text.Length > 0)
                {
                    break;
                }
            }

            TurnResult next = await session.RunTurnAsync("again", Ct);
            await session.FlushTranscriptAsync();

            // Assert
            Assert.Equal(1, next.TurnIndex);
            Assert.Equal(["hi", "Hel", "again", "next"], Texts(session.Transcript));
            Assert.Equal(2, store.Appends);
        }

        // An interrupt before the running turn yields anything keeps that turn's user
        // message only, and the turn before it is untouched.
        [Fact]
        public async Task CutBeforeAnyContent_KeepsTheUserMessageOnly_AndLeavesThePreviousTurn()
        {
            // Arrange
            TurnScriptChatClient model = TurnScriptChatClient.Sequence(["first reply"], ["second ", "reply"]);
            model.GateBeforeCall = 1;
            (ConversationSession session, RecordingConversationStore store, _) = Create(model);
            _ = await session.RunTurnAsync("one", Ct);
            Task drain = Task.Run(
                async () =>
                {
                    await foreach (ChatResponseUpdate _ in session.RunTurnStreamingAsync("two", Ct))
                    {
                    }
                },
                Ct);
            await model.Gated.Task.WaitAsync(Ct);

            // Act
            bool recorded = session.Cut(1, new TurnCut(string.Empty, null));
            _ = model.Release.TrySetResult();
            await drain;
            await session.FlushTranscriptAsync();

            // Assert
            Assert.True(recorded);
            Assert.Equal(["one", "first reply", "two"], Texts(session.Transcript));
            Assert.Empty(store.Rewrites);
            Assert.Equal(1, session.LastTurn!.TurnIndex);
            Assert.Equal(string.Empty, session.LastTurn.ReplyText);
        }

        // StartTurnAsync hands back the turn's index before the reply is read, and the
        // reply is the turn's streamed updates.
        [Fact]
        public async Task StartTurn_NamesTheTurnBeforeItsReplyIsRead()
        {
            // Arrange
            (ConversationSession session, _, _) = Create(TurnScriptChatClient.Sequence(["first reply"], ["second ", "reply"]));
            _ = await session.RunTurnAsync("one", Ct);

            // Act
            await using TurnRun run = await session.StartTurnAsync(new ChatMessage(ChatRole.User, "two"), origin: null, Ct);
            List<string> texts = [];
            await foreach (ChatResponseUpdate update in run.Updates)
            {
                // The trailing update is the turn's committed ids, not text.
                if (update.Contents.OfType<TurnCommittedContent>().Any())
                {
                    continue;
                }

                texts.Add(update.Text);
            }

            // Assert
            Assert.Equal(1, run.TurnIndex);
            Assert.Equal(["second ", "reply"], texts);
            Assert.Equal(1, session.LastTurn!.TurnIndex);
        }

        // On the voice seam: a cut that reaches a started turn before its reply is read keeps the
        // user message only, and the turn before it is untouched.
        [Fact]
        public async Task CutBeforeTheReplyIsRead_KeepsTheUserMessageOnly_AndLeavesThePreviousTurn()
        {
            // Arrange
            (ConversationSession session, RecordingConversationStore store, _) = Create(TurnScriptChatClient.Sequence(["first reply"], ["second reply"]));
            _ = await session.RunTurnAsync("one", Ct);
            await using TurnRun run = await session.StartTurnAsync(new ChatMessage(ChatRole.User, "two"), origin: null, Ct);

            // Act
            bool recorded = session.Cut(run.TurnIndex, new TurnCut(string.Empty, null));
            await foreach (ChatResponseUpdate _ in run.Updates)
            {
            }

            await session.FlushTranscriptAsync();

            // Assert
            Assert.True(recorded);
            Assert.Equal(["one", "first reply", "two"], Texts(session.Transcript));
            Assert.Equal(2, store.Appends);
            Assert.Empty(store.Rewrites);
            Assert.Equal(1, session.LastTurn!.TurnIndex);
            Assert.Equal(string.Empty, session.LastTurn.ReplyText);
        }

        // A cut of a committed turn rewrites its reply, never appends, and raises
        // reply.interrupted amending turn.completed, with no duration when nothing played.
        [Fact]
        public async Task CutOfACommittedTurn_RewritesTheReply_AndAmendsTurnCompletedWithoutADuration()
        {
            // Arrange
            (ConversationSession session, RecordingConversationStore store, InMemoryAuditSink sink) = Create(TurnScriptChatClient.Text("Hello ", "there"));
            _ = await session.RunTurnAsync("hi", Ct);

            // Act
            bool recorded = session.Cut(0, new TurnCut("Hello", Played: null));
            await session.FlushTranscriptAsync();

            // Assert
            Assert.True(recorded);
            Assert.Equal(["hi", "Hello"], Texts(session.Transcript));
            Assert.Equal(1, store.Appends);
            _ = Assert.Single(store.Rewrites);
            IReadOnlyList<AuditEvent> events = await session.RowsAsync(sink);
            AuditEvent completed = Assert.Single(events, item => item.Kind == AuditEventKind.TurnCompleted);
            AuditEvent amendment = Assert.Single(events, item => item.Kind == AuditEventKind.ReplyInterrupted);
            Assert.Equal(completed.EventId, amendment.AmendsEventId);
            Assert.Equal(AuditHash.OfText("Hello").Value, amendment.Payload[AuditPayloadKeys.UtteranceUntilInterruptSha256]);
            Assert.False(amendment.Payload.ContainsKey(AuditPayloadKeys.DurationUntilInterruptMs));
        }

        // An unknown or older turn: false, and nothing changes.
        [Fact]
        public async Task CutOfAnUnknownOrOlderTurn_ReportsFalseAndChangesNothing()
        {
            // Arrange
            (ConversationSession session, RecordingConversationStore store, _) = Create(TurnScriptChatClient.Sequence(["a1"], ["a2"]));
            _ = await session.RunTurnAsync("q1", Ct);
            _ = await session.RunTurnAsync("q2", Ct);

            // Act
            bool older = session.Cut(0, new TurnCut("a", null));
            bool unknown = session.Cut(7, new TurnCut("a", null));
            await session.FlushTranscriptAsync();

            // Assert
            Assert.False(older);
            Assert.False(unknown);
            Assert.Empty(store.Rewrites);
            Assert.Equal(["q1", "a1", "q2", "a2"], Texts(session.Transcript));
        }

        // A cut racing the turn's completion, 300 runs. Always one append and at most one rewrite, the
        // final text is the shown text, and both branches (cut while running, rewrite after the seal) are hit.
        [Fact]
        public async Task CutRacesCompletion_AlwaysOneAppend_AtMostOneRewrite_FinalTextIsShown_BothBranchesHit()
        {
            Random random = new(7);
            int running = 0;
            int amended = 0;
            for (int i = 0; i < 300; i++)
            {
                // Arrange
                TurnScriptChatClient model = TurnScriptChatClient.Text("Hel", "lo");
                model.FragmentDelayMs = 2;
                (ConversationSession session, RecordingConversationStore store, _) = Create(model);
                int wait = random.Next(0, 12);

                // Act
                Task run = Task.Run(
                    async () =>
                    {
                        await foreach (ChatResponseUpdate _ in session.RunTurnStreamingAsync("hi", Ct))
                        {
                        }
                    },
                    Ct);
                Task<bool> cut = Task.Run(
                    async () =>
                    {
                        await model.Called.Task.WaitAsync(Ct);
                        await Task.Delay(wait, Ct);
                        return session.Cut(0, new TurnCut("Hel", null));
                    },
                    Ct);
                await Task.WhenAll(run, cut);
                await session.FlushTranscriptAsync();

                // Assert
                Assert.True(await cut);
                Assert.Equal(1, store.Appends);
                Assert.True(store.Rewrites.Count <= 1);
                Assert.Equal(["hi", "Hel"], Texts(session.Transcript));
                Assert.Equal(["hi", "Hel"], Texts(store.Live(session.ConversationId).Select(row => row.Content)));
                if (store.Rewrites.Count == 0)
                {
                    running++;
                }
                else
                {
                    amended++;
                }
            }

            Assert.True(running > 0, $"running={running}");
            Assert.True(amended > 0, $"amended={amended}");
        }

        // Disposing a run nobody read frees the conversation, and the turn commits nothing.
        [Fact]
        public async Task AnUnreadRunDisposed_FreesTheConversationForTheNextTurn()
        {
            // Arrange
            (ConversationSession session, _, _) = Create(TurnScriptChatClient.Sequence(["first reply"], ["second reply"]));
            _ = await session.RunTurnAsync("one", Ct);
            TurnRun unread = await session.StartTurnAsync(new ChatMessage(ChatRole.User, "two"), origin: null, Ct);

            // Act
            await unread.DisposeAsync();
            TurnResult next = await session.RunTurnAsync("three", Ct);
            await session.FlushTranscriptAsync();

            // Assert
            Assert.Equal("second reply", next.ReplyText);
            Assert.Equal(["one", "first reply", "three", "second reply"], Texts(session.Transcript));
        }

        // A half-read run disposed frees the conversation, and disposing it twice is safe.
        [Fact]
        public async Task AHalfReadRunDisposedTwice_FreesTheConversationForTheNextTurn()
        {
            // Arrange
            (ConversationSession session, _, _) = Create(TurnScriptChatClient.Sequence(["Hel", "lo"], ["next reply"]));
            TurnRun run = await session.StartTurnAsync(new ChatMessage(ChatRole.User, "one"), origin: null, Ct);
            IAsyncEnumerator<ChatResponseUpdate> reader = run.Updates.GetAsyncEnumerator(Ct);
            Assert.True(await reader.MoveNextAsync());

            // Act
            await run.DisposeAsync();
            await run.DisposeAsync();
            TurnResult next = await session.RunTurnAsync("two", Ct);

            // Assert
            Assert.Equal("next reply", next.ReplyText);
        }
    }
}
