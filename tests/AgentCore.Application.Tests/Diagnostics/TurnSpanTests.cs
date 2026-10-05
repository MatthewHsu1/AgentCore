using AgentCore.TestSupport;
using AgentCore.Application.Diagnostics;
using AgentCore.Application.Tests.Runtime;
using Microsoft.Extensions.AI;
using System.Diagnostics;
using Xunit;
using AgentCore.Application.Runtime.Session;
using static AgentCore.Application.Tests.Diagnostics.TurnObservabilityHarness;

namespace AgentCore.Application.Tests.Diagnostics
{
    /// <summary>
    /// A turn is one span. A high-cardinality value belongs here and nowhere else.
    /// </summary>
    public sealed class TurnSpanTests
    {
        [Fact]
        public async Task ATurn_IsOneSpanThatCarriesTheConversationIdAsAGenAiAttribute()
        {
            string conversationId = "conversation-" + Guid.NewGuid().ToString("N");
            List<Activity> spans = await RecordSpansAsync(conversationId);

            Activity span = Assert.Single(spans);

            Assert.Equal(AgentCoreTelemetry.TurnActivityName, span.OperationName);

            // gen_ai.conversation.id is the
            // convention's own name for the conversation a request belongs to. A conversation is that
            // conversation, and a span attribute costs no series.
            Assert.Equal(conversationId, span.GetTagItem("gen_ai.conversation.id"));
            Assert.Equal(0, span.GetTagItem("agentcore.turn.index"));
            Assert.Equal("greeting", span.GetTagItem("agentcore.stage.before"));
            Assert.Equal("greeting", span.GetTagItem("agentcore.stage.after"));
            Assert.Equal("completed", span.GetTagItem("agentcore.turn.outcome"));
            Assert.Equal(ActivityStatusCode.Unset, span.Status);
        }

        [Fact]
        public async Task AFailedTurn_MarksItsSpanWithTheReasonOfSectionEightSeven()
        {
            List<Activity> spans = [];
            using ActivityListener listener = ListenTo(spans);

            using SequencedChatClient reply = new("   ");
            using SequencedChatClient fill = new(StayingNull);
            ConversationSession session = Build(PolicyYaml, reply, fill).Create("conversation-" + Guid.NewGuid().ToString("N"));

            _ = await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

            Activity span = Assert.Single(Snapshot(spans), item => string.Equals(
                item.GetTagItem("gen_ai.conversation.id") as string, session.ConversationId, StringComparison.Ordinal));

            Assert.Equal("failed", span.GetTagItem("agentcore.turn.outcome"));
            Assert.Equal(ActivityStatusCode.Error, span.Status);

            // The span carries only the closed failure-kind token, never TurnFailureReasons.EmptyReply's
            // own text: nothing threw here, so there is no exception type to report, and the fixed reason
            // stays off the span the same way a raw exception message would (spans carry the type,
            // logs carry the full error).
            Assert.Equal(AgentCoreTelemetry.FailureEmptyReply, span.StatusDescription);
            Assert.Equal(AgentCoreTelemetry.FailureEmptyReply, span.GetTagItem("error.type"));
        }

        [Fact]
        public async Task AStreamedTurn_IsOneSpanToo()
        {
            List<Activity> spans = [];
            using ActivityListener listener = ListenTo(spans);

            using SequencedChatClient reply = new("hello there.");
            using SequencedChatClient fill = new(StayingNull);
            ConversationSession session = Build(PolicyYaml, reply, fill).Create("conversation-" + Guid.NewGuid().ToString("N"));

            await foreach (ChatResponseUpdate update in session.RunTurnStreamingAsync("hi", TestContext.Current.CancellationToken))
            {
                Assert.NotNull(update);
            }

            // The span travels on the turn record and not on Activity.Current, because an async iterator
            // restores the execution context of its caller at every yield.
            Activity span = Assert.Single(Snapshot(spans), item => string.Equals(
                item.GetTagItem("gen_ai.conversation.id") as string, session.ConversationId, StringComparison.Ordinal));

            Assert.Equal("completed", span.GetTagItem("agentcore.turn.outcome"));
        }

        /// <summary>Runs one turn and collects the spans of this library.</summary>
        private static async Task<List<Activity>> RecordSpansAsync(string conversationId)
        {
            List<Activity> spans = [];
            using ActivityListener listener = ListenTo(spans);

            using SequencedChatClient reply = new("hello there.");
            using SequencedChatClient fill = new(StayingNull);
            ConversationSession session = Build(PolicyYaml, reply, fill).Create(conversationId);

            _ = await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

            return [.. Snapshot(spans).Where(span => string.Equals(
                span.GetTagItem("gen_ai.conversation.id") as string, conversationId, StringComparison.Ordinal))];
        }
    }
}
