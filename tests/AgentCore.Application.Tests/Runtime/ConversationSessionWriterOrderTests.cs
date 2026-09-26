using AgentCore.Application.Runtime;
using AgentCore.Application.State;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Domain;
using Microsoft.Extensions.AI;
using Xunit;
using static AgentCore.Application.Tests.Runtime.ConversationSessionTestSupport;

namespace AgentCore.Application.Tests.Runtime
{
    /// <summary>The writers, in their fixed order: const, counter, tool, then extractor.</summary>
    public sealed class ConversationSessionWriterOrderTests
    {
        // The writers, in order.
        [Fact]
        public void TheConstWriter_RunsOnceWhenTheSessionStarts()
        {
            using SequencedChatClient reply = new("hello there.");
            using SequencedChatClient fill = new(StayingNull);

            ConversationSession session = Build(PolicyYaml, reply, fill).Create();

            // A constant never changes, so it is written before the first turn and never again.
            Assert.Equal("sole", session.State.Read("brand")!.GetValue<string>());
            Assert.False(session.State.IsUnfilled("brand"));
        }

        [Fact]
        public async Task TheCounter_RunsBeforeTheTransitionAndReadsTheStageTheTurnSpokeIn()
        {
            using SequencedChatClient reply = new("hello there.");
            using SequencedChatClient fill = new(SaidGoodbye);
            ConversationSession session = Build(PolicyYaml, reply, fill).Create();

            TurnResult turn = await session.RunTurnAsync("goodbye", TestContext.Current.CancellationToken);

            // The turn spoke in greeting and the machine then moved to close. The counter rule reads
            // stage === greeting, so a counter that ran after the transition would report zero.
            Assert.Equal("greeting", turn.StageBefore);
            Assert.Equal("close", turn.StageAfter);
            Assert.Equal(1, session.State.Read("greetingTurns")!.GetValue<long>());
        }

        [Fact]
        public async Task TheToolWriter_RunsBeforeTheCounterThatReadsItsSlot()
        {
            using ToolCallingChatClient reply = new("your order is on the way.");
            using SequencedChatClient fill = new(StayingNull);
            ConversationSession session = Build(ToolYaml, reply, fill, new StubToolBuilder(/*lang=json,strict*/ """{ "status": "shipped" }""").Create).Create();

            TurnResult turn = await session.RunTurnAsync("where is my order", TestContext.Current.CancellationToken);

            Assert.Equal(["lookup_order"], reply.Called);
            Assert.Equal("shipped", session.State.Read("orderStatus")!.GetValue<string>());

            // The counter rule reads orderStatus. It is one only because the tool writer went first.
            Assert.Equal(1, session.State.Read("shippedTurns")!.GetValue<long>());
            Assert.Equal("greeting", turn.StageAfter);
        }

        [Fact]
        public async Task TheToolWriter_ReadsAResultTheToolAnsweredAsText()
        {
            using ToolCallingChatClient reply = new("your order is on the way.");
            using SequencedChatClient fill = new(StayingNull);
            StubToolBuilder tools = new(/*lang=json,strict*/ """{ "status": "shipped" }""", asText: true);
            ConversationSession session = Build(ToolYaml, reply, fill, tools.Create).Create();

            _ = await session.RunTurnAsync("where is my order", TestContext.Current.CancellationToken);

            // A tool result has no declared shape, so a document answered as one string still resolves.
            Assert.Equal("shipped", session.State.Read("orderStatus")!.GetValue<string>());
        }

        [Fact]
        public async Task TheExtractor_RunsBeforeTheTransitionThatReadsItsSlot()
        {
            using SequencedChatClient reply = new("hello there.");
            using SequencedChatClient fill = new(SaidGoodbye);
            ConversationSession session = Build(PolicyYaml, reply, fill).Create();

            TurnResult turn = await session.RunTurnAsync("goodbye", TestContext.Current.CancellationToken);

            // The guard reads callerSaidGoodbye, and only the extractor writes it.
            Assert.True(session.State.Read("callerSaidGoodbye")!.GetValue<bool>());
            Assert.Equal("close", turn.StageAfter);
            Assert.True(turn.IsTerminal);
            Assert.True(session.IsComplete);
        }

        [Fact]
        public async Task TheExtractor_ReadsTheFinishedTurn()
        {
            using SequencedChatClient reply = new("hello there.");
            using SequencedChatClient fill = new(StayingNull);
            ConversationSession session = Build(PolicyYaml, reply, fill).Create();

            _ = await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

            // extractor.when: after_reply. The request holds what the caller said and what the agent
            // answered, and the caller's message carries no reminder.
            List<ChatMessage> request = fill.Requests[0];
            Assert.Contains(request, message => Contains(message, "hi"));
            Assert.Contains(request, message => Contains(message, "hello there."));
            Assert.DoesNotContain(request, message => Contains(message, UnfilledSlotReminder.OpenTag));
        }

        [Fact]
        public async Task TheExtractor_ReadsTheAgentsPreviousMessageBeforeTheFinishedTurn()
        {
            using SequencedChatClient reply = new("is it the CT900 or the CT900ENT?", "got it.");
            using SequencedChatClient fill = new(StayingNull, StayingNull);
            ConversationSession session = Build(PolicyYaml, reply, fill).Create();

            _ = await session.RunTurnAsync("my belt slips", TestContext.Current.CancellationToken);
            _ = await session.RunTurnAsync("the ENT one", TestContext.Current.CancellationToken);

            // Turn 1 has no earlier agent message, so the finished turn stands alone.
            List<ChatMessage> first = [.. fill.Requests[0].Where(message => message.Role != ChatRole.System)];
            Assert.Equal([ChatRole.User, ChatRole.Assistant], first.Select(message => message.Role));

            // Turn 2: the caller is answering a question. Measured 2026-09-02: without the question in
            // view the extractor reads "the ENT one" and fills nothing; with it, it fills the machine.
            List<ChatMessage> second = [.. fill.Requests[1].Where(message => message.Role != ChatRole.System)];
            Assert.Equal([ChatRole.Assistant, ChatRole.User, ChatRole.Assistant], second.Select(message => message.Role));
            Assert.Equal("is it the CT900 or the CT900ENT?", second[0].Text);
            Assert.Equal("the ENT one", second[1].Text);
            Assert.Equal("got it.", second[2].Text);
        }

        [Fact]
        public async Task AFailedExtraction_DoesNotDropTheTurn()
        {
            using SequencedChatClient reply = new("hello there.");
            using SequencedChatClient fill = new("I am sorry, I cannot do that.");
            ConversationSession session = Build(PolicyYaml, reply, fill).Create();

            TurnResult turn = await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

            // Section 8.7: the extractor never drops a conversation. The turn ends, and it carries the reason.
            Assert.NotNull(turn.ExtractionFailure);
            Assert.Equal("hello there.", turn.ReplyText);
            Assert.Equal("greeting", turn.StageAfter);
            Assert.True(session.State.IsUnfilled("callerSaidGoodbye"));
        }

        [Fact]
        public async Task ADocumentWithNoExtractor_RunsATurnAndReportsNoFailure()
        {
            using SequencedChatClient reply = new("first reply.");
            ConversationSession session = Build(TwoStagesYaml, reply, null).Create();

            TurnResult turn = await session.RunTurnAsync("one", TestContext.Current.CancellationToken);

            Assert.Null(turn.ExtractionFailure);
            Assert.Equal(1, reply.Calls);
        }
    }
}
