using AgentCore.Application.Runtime;
using AgentCore.Application.Runtime.Turn;
using AgentCore.Domain;
using Microsoft.Extensions.AI;
using Xunit;
using static AgentCore.Application.Tests.Runtime.ConversationSessionTestSupport;

namespace AgentCore.Application.Tests.Runtime
{
    /// <summary>Section 8.7, last row: the run returns quietly with no text.</summary>
    public sealed class ConversationSessionEmptyReplyTests
    {
        [Fact]
        public async Task AnEmptyReply_SpeaksTheFallbackAndReportsWhyTheTurnFailed()
        {
            using SequencedChatClient reply = new("   ");
            using SequencedChatClient fill = new(StayingNull);
            ConversationSession session = Build(PolicyYaml, reply, fill).Create();

            TurnResult turn = await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

            // Request 41 goes out with no tools and the run returns with no exception. On a voice conversation
            // that is silence, so the turn loop reads the reply rather than trusting the absence of a
            // failure.
            Assert.Equal(ConversationSession.FallbackReply, turn.ReplyText);
            Assert.Equal(ConversationSession.EmptyReplyReason, turn.Failure);
            Assert.Null(turn.Cut);

            // The writers still ran, and the transcript holds what the caller heard.
            Assert.Equal(1, session.State.TurnIndex);
            Assert.Equal(1, session.State.Read("greetingTurns")!.GetValue<long>());
            Assert.Equal(ConversationSession.FallbackReply, session.Transcript[^1].Text);
        }

        [Fact]
        public async Task AnEmptyReply_LeavesTheSessionReadyForTheNextTurn()
        {
            using SequencedChatClient reply = new("", "I am back.");
            using SequencedChatClient fill = new(StayingNull);
            ConversationSession session = Build(PolicyYaml, reply, fill).Create();

            TurnResult first = await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);
            TurnResult second = await session.RunTurnAsync("still there?", TestContext.Current.CancellationToken);

            Assert.Equal(ConversationSession.FallbackReply, first.ReplyText);
            Assert.Equal("I am back.", second.ReplyText);
            Assert.Null(second.Failure);
            Assert.Equal(1, second.TurnIndex);
        }

        [Fact]
        public async Task Streaming_SpeaksTheFallbackWhenTheRunProducesNoText()
        {
            using LifecycleChatClient reply = new();
            ConversationSession session = Build(TwoStagesYaml, reply, null).Create();

            List<ChatResponseUpdate> updates = [];
            await foreach (ChatResponseUpdate update in session.RunTurnStreamingAsync("hi", TestContext.Current.CancellationToken))
            {
                updates.Add(update);
            }

            // The host hears the fallback rather than nothing at all: R2 makes a quiet run an ordinary
            // successful one, and the spoken line leaves the seam the same way any reply does. The
            // trailing update is the turn's committed ids (design section 6, step E5), not more text.
            Assert.Equal(
                [ConversationSession.FallbackReply],
                updates.Where(update => !update.Contents.OfType<TurnCommittedContent>().Any()).Select(update => update.Text));
            Assert.NotNull(session.LastTurn);
            Assert.Equal(ConversationSession.FallbackReply, session.LastTurn.ReplyText);
            Assert.Equal(ConversationSession.EmptyReplyReason, session.LastTurn.Failure);
            Assert.Equal("close", session.Stage);
        }
    }
}
