using AgentCore.Application.Runtime;
using AgentCore.Domain;
using Microsoft.Extensions.AI;
using Xunit;
using static AgentCore.Application.Tests.Runtime.ConversationSessionTestSupport;

namespace AgentCore.Application.Tests.Runtime
{
    /// <summary>
    /// The turn loop's one-turn contract: what a turn reports, and what the transcript it owns holds
    /// and hands out.
    /// </summary>
    public sealed class ConversationSessionTranscriptTests
    {
        // One turn, end to end.
        [Fact]
        public async Task ATurn_RunsTheAgentOfTheCurrentStageAndReportsWhatItDid()
        {
            using SequencedChatClient reply = new("hello there.");
            using SequencedChatClient fill = new(StayingNull);
            ConversationSession session = Build(PolicyYaml, reply, fill).Create("conversation-1");

            TurnResult turn = await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

            Assert.Equal("conversation-1", turn.ConversationId);
            Assert.Equal(0, turn.TurnIndex);
            Assert.Equal("greeting", turn.StageBefore);
            Assert.Equal("greeting", turn.StageAfter);
            Assert.Equal("hello there.", turn.ReplyText);
            Assert.False(turn.IsTerminal);
            Assert.Null(turn.ExtractionFailure);
            Assert.Same(turn, session.LastTurn);
        }

        [Fact]
        public async Task ATurn_MakesTwoModelCalls_TheReplyAndTheExtractor()
        {
            using SequencedChatClient reply = new("hello there.");
            using SequencedChatClient fill = new(StayingNull);
            ConversationSession session = Build(PolicyYaml, reply, fill).Create();

            _ = await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

            // The extractor has no retry: one reply conversation, and one extractor call.
            Assert.Equal(1, reply.Calls);
            Assert.Equal(1, fill.Calls);
        }

        [Fact]
        public async Task TheTranscript_HoldsWhatTheCallerSaidAndWhatTheAgentAnswered()
        {
            using SequencedChatClient reply = new("hello there.");
            using SequencedChatClient fill = new(StayingNull);
            ConversationSession session = Build(PolicyYaml, reply, fill).Create();

            _ = await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

            Assert.Equal(2, session.Transcript.Count);
            Assert.Equal(ChatRole.User, session.Transcript[0].Role);
            Assert.Equal("hi", session.Transcript[0].Text);
            Assert.Equal(ChatRole.Assistant, session.Transcript[1].Role);
            Assert.Equal("hello there.", session.Transcript[1].Text);
        }

        [Fact]
        public async Task TheTranscript_HandsOutACopyAndNeverTheListTheSessionWritesTo()
        {
            using SequencedChatClient reply = new("hello there.", "and again.");
            using SequencedChatClient fill = new(StayingNull, StayingNull);
            ConversationSession session = Build(PolicyYaml, reply, fill).Create();

            _ = await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);
            IReadOnlyList<ChatMessage> taken = session.Transcript;

            _ = await session.RunTurnAsync("still there?", TestContext.Current.CancellationToken);

            // A live view would grow behind the reader's back, and an amendment splicing the list under
            // an enumeration would throw rather than return a torn conversation. What was handed out is
            // one whole conversation as it stood at one instant, and the session went on without it.
            Assert.Equal(2, taken.Count);
            Assert.Equal(4, session.Transcript.Count);
            Assert.NotSame(taken, session.Transcript);
        }

        // The session owns the transcript, and the stage switches the agent.
        [Fact]
        public async Task TheStage_SwitchesTheAgentAndTheTranscriptCarriesTheWholeConversation()
        {
            using SequencedChatClient reply = new("first reply.", "second reply.");
            ConversationSession session = Build(TwoStagesYaml, reply, null).Create();

            _ = await session.RunTurnAsync("one", TestContext.Current.CancellationToken);
            Assert.Equal("close", session.Stage);

            _ = await session.RunTurnAsync("two", TestContext.Current.CancellationToken);
            Assert.Equal("greeting", session.Stage);

            // Each stage names its own agent, so the instructions change between the two requests.
            Assert.Contains("I am the greeter", reply.Options[0]!.Instructions, StringComparison.Ordinal);
            Assert.Contains("I am the closer", reply.Options[1]!.Instructions, StringComparison.Ordinal);

            // A session bound to one agent could not carry this. The turn loop owns the transcript, so
            // the second agent reads the first turn.
            Assert.Contains(reply.Requests[1], message => Contains(message, "one"));
            Assert.Contains(reply.Requests[1], message => Contains(message, "first reply."));
        }
    }
}
