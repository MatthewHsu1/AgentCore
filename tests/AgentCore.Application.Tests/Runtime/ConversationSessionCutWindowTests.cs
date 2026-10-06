using AgentCore.Application.Audit.Memory;
using AgentCore.Application.Runtime.Turn;
using AgentCore.Application.Tests.Audit;
using AgentCore.Application.Tests.Fakes;
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
    /// <see cref="ConversationSession.Cut"/> at the edges of a turn's window: after its commit, after its run was
    /// abandoned, and after the conversation ended.
    /// </summary>
    public sealed class ConversationSessionCutWindowTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        // A cut after the commit belongs to the committed turn. It lands while the reader still
        // holds the turn's last update, so the run has not ended, and it rewrites the reply the commit wrote.
        [Fact]
        public async Task ACutAfterTheCommit_WhileTheRunIsStillOpen_RewritesTheCommittedReply()
        {
            // Arrange
            (ConversationSession session, RecordingConversationStore store, InMemoryAuditSink sink) = Create(TurnScriptChatClient.Text("Hello ", "there"));
            await using TurnRun run = await session.StartTurnAsync(new ChatMessage(ChatRole.User, "hi"), origin: null, Ct);

            // Act
            bool? recorded = null;
            await foreach (ChatResponseUpdate update in run.Updates)
            {
                if (update.Contents.OfType<TurnCommittedContent>().Any())
                {
                    recorded = session.Cut(run.TurnIndex, new TurnCut("Hello", Played: null));
                }
            }

            await session.FlushTranscriptAsync();

            // Assert
            Assert.True(recorded);
            Assert.Equal(["hi", "Hello"], Texts(session.Transcript));
            Assert.Equal(1, store.Appends);
            _ = Assert.Single(store.Rewrites);
            _ = Assert.Single(await session.RowsAsync(sink), item => item.Kind == AuditEventKind.ReplyInterrupted);
        }

        // A run disposed unread commits nothing, so a cut that names it has no turn to reach.
        [Fact]
        public async Task ACutOfARunDisposedUnread_ReportsFalse_AndChangesNothing()
        {
            // Arrange
            (ConversationSession session, RecordingConversationStore store, _) = Create(TurnScriptChatClient.Sequence(["first reply"], ["second reply"]));
            _ = await session.RunTurnAsync("one", Ct);
            TurnRun unread = await session.StartTurnAsync(new ChatMessage(ChatRole.User, "two"), origin: null, Ct);
            await unread.DisposeAsync();

            // Act
            bool recorded = session.Cut(unread.TurnIndex, new TurnCut("sec", null));
            await session.FlushTranscriptAsync();

            // Assert
            Assert.False(recorded);
            Assert.Equal(["one", "first reply"], Texts(session.Transcript));
            Assert.Empty(store.Rewrites);
        }

        // AmendTurn: nothing may be appended behind conversation.ended, so a cut after the end records nothing.
        [Fact]
        public async Task ACutAfterTheConversationEnded_ReportsFalse_AndChangesNothing()
        {
            // Arrange
            (ConversationSession session, RecordingConversationStore store, InMemoryAuditSink sink) = Create(TurnScriptChatClient.Text("Hello ", "there"));
            _ = await session.RunTurnAsync("hi", Ct);
            _ = session.EndConversation(ConversationEndReason.CallerHungUp);

            // Act
            bool recorded = session.Cut(0, new TurnCut("Hello", null));
            await session.FlushTranscriptAsync();

            // Assert
            Assert.False(recorded);
            Assert.Empty(store.Rewrites);
            Assert.Equal(AuditEventKind.ConversationEnded, (await session.RowsAsync(sink))[^1].Kind);
            Assert.DoesNotContain(await session.RowsAsync(sink), item => item.Kind == AuditEventKind.ReplyInterrupted);
        }

        // IConversationPort.Cut: a negative played duration is refused.
        [Fact]
        public async Task ACutWithANegativePlayedDuration_IsRefused()
        {
            // Arrange
            (ConversationSession session, RecordingConversationStore store, _) = Create(TurnScriptChatClient.Text("Hello ", "there"));
            _ = await session.RunTurnAsync("hi", Ct);

            // Act
            _ = Assert.Throws<ArgumentOutOfRangeException>(() => session.Cut(0, new TurnCut("Hello", TimeSpan.FromMilliseconds(-1))));
            await session.FlushTranscriptAsync();

            // Assert
            Assert.Empty(store.Rewrites);
            Assert.Equal(["hi", "Hello there"], Texts(session.Transcript));
        }

        // ConversationSession.LastReplyMessageId names the last message written. A turn cut before any word wrote
        // only the user's message, so that is the one an edit hangs off.
        [Fact]
        public async Task ATurnCutBeforeAnyWord_NamesTheUserMessageAsTheLastWritten()
        {
            // Arrange
            (ConversationSession session, RecordingConversationStore store, _) = Create(TurnScriptChatClient.Sequence(["first reply"], ["second reply"]));
            _ = await session.RunTurnAsync("one", Ct);
            await using TurnRun run = await session.StartTurnAsync(new ChatMessage(ChatRole.User, "two"), origin: null, Ct);

            // Act
            _ = session.Cut(run.TurnIndex, new TurnCut(string.Empty, null));
            await foreach (ChatResponseUpdate _ in run.Updates)
            {
            }

            await session.FlushTranscriptAsync();

            // Assert
            Assert.Equal("two", store.Rows[^1].Content.Text);
            Assert.Equal(store.Rows[^1].MessageId, session.LastReplyMessageId);
        }
    }
}
