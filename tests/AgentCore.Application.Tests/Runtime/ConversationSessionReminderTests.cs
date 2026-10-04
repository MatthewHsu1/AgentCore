using AgentCore.Application.State;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Application.Runtime.Session;
using static AgentCore.Application.Tests.Runtime.ConversationSessionTestSupport;

namespace AgentCore.Application.Tests.Runtime
{
    /// <summary>The unfilled-slot reminder: when it rides the reply request, and when it does not.</summary>
    public sealed class ConversationSessionReminderTests
    {
        // The reminder rides one request.
        [Fact]
        public async Task TheReminder_ReachesTheReplyAgentAndLeavesTheCallersMessageAlone()
        {
            using SequencedChatClient reply = new("hello there.");
            using SequencedChatClient fill = new(StayingNull);
            ConversationSession session = Build(ReminderYaml, reply, fill).Create();

            _ = await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

            // The stage waits on two slots the caller supplies, and no writer has filled either yet.
            Assert.Contains(UnfilledSlotReminder.OpenTag, reply.SystemText(0), StringComparison.Ordinal);
            Assert.Contains(
                "the machine model and the serial number", reply.SystemText(0), StringComparison.Ordinal);

            // The caller's utterance is the caller's. It goes to the model, to the message store and to the
            // extractor as it was spoken.
            Assert.Equal("hi", reply.LastUserText(0));
            Assert.Equal("hi", session.Transcript[0].Text);
        }

        [Fact]
        public async Task TheReminder_DropsASlotOnceAWriterFillsIt()
        {
            using SequencedChatClient reply = new("hello there.", "still here.");
            using SequencedChatClient fill = new(/*lang=json,strict*/ """{ "machineModel": "F85" }""", StayingNull);
            ConversationSession session = Build(ReminderYaml, reply, fill).Create();

            _ = await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);
            _ = await session.RunTurnAsync("still there?", TestContext.Current.CancellationToken);

            // Turn 2 still waits on the serial number, so the reminder survives and names only that one.
            Assert.Contains(
                "the machine model and the serial number", reply.SystemText(0), StringComparison.Ordinal);
            Assert.Contains(UnfilledSlotReminder.OpenTag, reply.SystemText(1), StringComparison.Ordinal);
            Assert.Contains("the serial number.", reply.SystemText(1), StringComparison.Ordinal);
            Assert.DoesNotContain("the machine model", reply.SystemText(1), StringComparison.Ordinal);
        }

        [Fact]
        public async Task TheReminder_ReachesAStreamingTurnToo()
        {
            // The run is inside the async iterator, and an iterator restores its caller's execution
            // context at every yield. A per-turn ambient value has to be opened again for each round or
            // the streaming path silently loses the reminder the buffered path carries.
            using SequencedChatClient reply = new("hello there.");
            using SequencedChatClient fill = new(StayingNull);
            ConversationSession session = Build(ReminderYaml, reply, fill).Create();

            await foreach (ChatResponseUpdate _ in session.RunTurnStreamingAsync("hi", TestContext.Current.CancellationToken))
            {
            }

            Assert.Contains(UnfilledSlotReminder.OpenTag, reply.SystemText(0), StringComparison.Ordinal);
            Assert.Equal("hi", reply.LastUserText(0));
        }

        [Fact]
        public async Task TheReminder_NeverAsksTheCallerForAnInferredFlag()
        {
            // config/local.yaml ships this shape: one boolean the extractor infers from the turn, read by
            // the exit guard of the talking stage, carrying a default. The reminder fires on a slot that
            // is "still null", and a default is never null, so nothing is owed and the caller is not asked.
            using SequencedChatClient reply = new("hello there.");
            using SequencedChatClient fill = new(StayingNull);
            ConversationSession session = Build(PolicyYaml, reply, fill).Create();

            _ = await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

            string turnContext = reply.SystemText(0);
            Assert.DoesNotContain(UnfilledSlotReminder.OpenTag, turnContext, StringComparison.Ordinal);
            Assert.DoesNotContain("callerSaidGoodbye", turnContext, StringComparison.Ordinal);
        }
    }
}
