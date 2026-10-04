using AgentCore.Application.Tests.Fakes;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Application.Runtime.Cut;
using AgentCore.Application.Runtime.Session;
using AgentCore.Application.Runtime.Turn.Lifecycle;
using static AgentCore.Application.Tests.Runtime.ConversationSessionCutTestSupport;

namespace AgentCore.Application.Tests.Runtime
{
    /// <summary>
    /// <see cref="ConversationSession.Cut"/> on a turn that already committed.
    /// </summary>
    public sealed class ConversationSessionCommittedCutTests
    {
        private static readonly TimeSpan Stuck = TimeSpan.FromSeconds(10);

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        // A cut that shows nothing of a committed reply leaves it carrying nothing, so
        // the row goes, as a running turn's cut drops it; LiveKit adds a message only if forwarded_text
        // (agent_activity.py:3335). The next model is never read an assistant message with no content.
        [Fact]
        public async Task EmptyCutOfACommittedReply_RemovesTheReplyRow_AndTheNextModelReadsNoEmptyMessage()
        {
            // Arrange
            TurnScriptChatClient model = TurnScriptChatClient.Sequence(["Hello ", "there."], ["next reply"]);
            (ConversationSession session, RecordingConversationStore store, _) = Create(model);
            _ = await session.RunTurnAsync("hi", Ct);

            // Act
            bool recorded = session.Cut(0, new TurnCut(string.Empty, null));
            await session.FlushTranscriptAsync();
            List<ChatRole> stored = [.. store.Live(session.ConversationId).Select(row => row.Content.Role)];
            _ = await session.RunTurnAsync("and now?", Ct);

            // Assert
            Assert.True(recorded);
            Assert.Equal([ChatRole.User], stored);
            List<ChatMessage> sent = [.. model.Requests[1].Where(message => message.Role != ChatRole.System)];
            Assert.Equal(["hi", "and now?"], sent.Select(message => message.Text));
            Assert.All(sent, message => Assert.NotEmpty(message.Contents));
        }

        // Once turn 1 has started, a cut naming turn 0 is stale. It reports
        // false and turn 0's reply stays as it was saved, while turn 1 still runs and commits.
        [Fact]
        public async Task CutOfThePreviousTurn_WhileTheNextTurnRuns_IsRefusedAndChangesNothing()
        {
            // Arrange
            TurnScriptChatClient model = TurnScriptChatClient.Sequence(["a0"], ["a1"]);
            model.GateBeforeCall = 1;
            (ConversationSession session, RecordingConversationStore store, _) = Create(model);
            _ = await session.RunTurnAsync("q0", Ct);
            await using TurnRun running = await session.StartTurnAsync(new ChatMessage(ChatRole.User, "q1"), origin: null, Ct);
            Task runningRead = ReadToEndAsync(running, Ct);
            await model.Gated.Task.WaitAsync(Stuck, Ct);

            // Act
            bool stale = session.Cut(0, new TurnCut(string.Empty, null));
            _ = model.Release.TrySetResult();
            await runningRead.WaitAsync(Stuck, Ct);
            await session.FlushTranscriptAsync();

            // Assert
            Assert.False(stale);
            Assert.Empty(store.Rewrites);
            Assert.Equal(["q0", "a0", "q1", "a1"], Texts(store.Live(session.ConversationId).Select(row => row.Content)));
            Assert.Equal(["q0", "a0", "q1", "a1"], Texts(session.Transcript));
        }
    }
}
