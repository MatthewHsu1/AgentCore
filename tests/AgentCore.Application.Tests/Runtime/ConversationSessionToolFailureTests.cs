using AgentCore.Application.Runtime;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Domain;
using Microsoft.Extensions.AI;
using Xunit;
using static AgentCore.Application.Tests.Runtime.ConversationSessionTestSupport;

namespace AgentCore.Application.Tests.Runtime
{
    /// <summary>Section 8.7, sixth row: the 4th consecutive tool failure.</summary>
    public sealed class ConversationSessionToolFailureTests
    {
        [Fact]
        public async Task TheFourthConsecutiveToolFailure_EndsTheTurnAndNeverKillsTheConversation()
        {
            using LoopingToolCallingChatClient reply = new();
            using SequencedChatClient fill = new(StayingNull);
            ThrowingToolBuilder tools = new();
            ConversationSession session = Build(ToolYaml, reply, fill, tools.Create).Create();

            TurnResult turn = await session.RunTurnAsync("where is my order", TestContext.Current.CancellationToken);

            // MaximumConsecutiveErrorsPerRequest is 3, so the 4th failure throws out of the run.
            Assert.Equal(4, tools.Calls);
            Assert.Equal(ConversationSession.FallbackReply, turn.ReplyText);
            Assert.NotNull(turn.Failure);
            Assert.StartsWith(ConversationSession.ToolFailureReason, turn.Failure, StringComparison.Ordinal);
            Assert.Contains(ThrowingToolBuilder.Message, turn.Failure, StringComparison.Ordinal);

            // The writers ran in their fixed order, and the machine picked the stage of the next turn.
            Assert.Equal(1, session.State.TurnIndex);
            Assert.Equal("greeting", turn.StageAfter);
            Assert.False(session.IsComplete);

            // Engine design section 3, W07: a tool fault keeps the user message, the three rounds whose
            // result arrived, and the fallback. The 4th call never got a result, so it is dropped.
            Assert.Equal("where is my order", session.Transcript[0].Text);
            Assert.Equal(3, session.Transcript.SelectMany(message => message.Contents).OfType<FunctionCallContent>().Count());
            Assert.Equal(3, session.Transcript.SelectMany(message => message.Contents).OfType<FunctionResultContent>().Count());
            Assert.Equal(ConversationSession.FallbackReply, session.Transcript[^1].Text);
        }

        [Fact]
        public async Task ARealDeclaredToolThatCannotReachItsEndpoint_EndsTheTurnPerRowSixAndKeepsTheConversation()
        {
            // The same row, reached the way a shipped tool actually reaches it. ThrowingToolBuilder
            // throws straight at the framework; this goes through DeclaredTool, so the classification
            // AuditingFunctionInvokingChatClient applies is what lets the fault out.
            using LoopingToolCallingChatClient reply = new();
            using SequencedChatClient fill = new(StayingNull);
            UnreachableEndpointToolBuilder tools = new();
            ConversationSession session = Build(ToolYaml, reply, fill, tools.Create).Create();

            TurnResult turn = await session.RunTurnAsync("where is my order", TestContext.Current.CancellationToken);

            // MaximumConsecutiveErrorsPerRequest is 3, so the 4th failure throws out of the run.
            Assert.Equal(4, tools.Calls);
            Assert.Equal(ConversationSession.FallbackReply, turn.ReplyText);
            Assert.NotNull(turn.Failure);
            Assert.StartsWith(ConversationSession.ToolFailureReason, turn.Failure, StringComparison.Ordinal);
            Assert.Contains(UnreachableEndpointToolBuilder.Message, turn.Failure, StringComparison.Ordinal);

            // The writers ran in their fixed order, the machine picked the stage of the next turn, and
            // the conversation is still alive.
            Assert.Equal(1, session.State.TurnIndex);
            Assert.Equal("greeting", turn.StageAfter);
            Assert.False(session.IsComplete);
        }

        [Fact]
        public async Task ARealDeclaredToolWhoseFaultTheModelCanAnswer_CompletesTheTurnNormally()
        {
            // The half that must not regress. The tool answers with an error result, the model reads it,
            // and nothing about the turn is a failure: no budget spent, no fallback, a real reply.
            using ToolCallingChatClient reply = new("That order is already closed.");
            using SequencedChatClient fill = new(StayingNull);
            RefusedRequestToolBuilder tools = new();
            ConversationSession session = Build(ToolYaml, reply, fill, tools.Create).Create();

            TurnResult turn = await session.RunTurnAsync("where is my order", TestContext.Current.CancellationToken);

            Assert.Equal(1, tools.Calls);
            Assert.Null(turn.Failure);
            Assert.Equal("That order is already closed.", turn.ReplyText);
            Assert.False(session.IsComplete);
        }

        [Fact]
        public async Task TheFourthConsecutiveToolFailure_LeavesTheSessionUnlocked()
        {
            using LoopingToolCallingChatClient reply = new();
            using SequencedChatClient fill = new(StayingNull);
            ConversationSession session = Build(ToolYaml, reply, fill, new ThrowingToolBuilder().Create).Create();

            _ = await session.RunTurnAsync("where is my order", TestContext.Current.CancellationToken);

            // The _running flag went back to zero, so the next turn starts rather than throwing.
            TurnResult second = await session.RunTurnAsync("try again", TestContext.Current.CancellationToken);

            Assert.Equal(1, second.TurnIndex);
            Assert.Equal(ConversationSession.FallbackReply, second.ReplyText);
        }

        [Fact]
        public async Task Streaming_EndsTheTurnWhenTheFourthConsecutiveToolFailureThrows()
        {
            using LoopingToolCallingChatClient reply = new();
            using SequencedChatClient fill = new(StayingNull);
            ThrowingToolBuilder tools = new();
            ConversationSession session = Build(ToolYaml, reply, fill, tools.Create).Create();

            List<ChatResponseUpdate> updates = [];
            await foreach (ChatResponseUpdate update in session.RunTurnStreamingAsync(
                "where is my order", TestContext.Current.CancellationToken))
            {
                updates.Add(update);
            }

            // The enumeration ends rather than throwing, so the host never sees the fault.
            Assert.Equal(4, tools.Calls);
            Assert.NotNull(session.LastTurn);
            Assert.Equal(ConversationSession.FallbackReply, session.LastTurn.ReplyText);
            Assert.StartsWith(ConversationSession.ToolFailureReason, session.LastTurn.Failure!, StringComparison.Ordinal);
            Assert.Equal(1, session.State.TurnIndex);
        }
    }
}
