using AgentCore.Application.Conversation.Memory;
using AgentCore.Application.Hooks;
using AgentCore.Application.Hooks.Gates;
using AgentCore.Application.Hooks.Notices;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Transcript;
using AgentCore.Domain;
using AgentCore.TestSupport;
using Xunit;
using AgentCore.Application.Runtime.Session;
using static AgentCore.Application.Tests.Transcript.ConversationSessionCompactionTestSupport;

namespace AgentCore.Application.Tests.Hooks
{
    public sealed class CompactionGateTests
    {
        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        // The hook's text is the summary row; the summariser is not asked.
        [Fact]
        public async Task AReplacedSummaryIsTheRowThatIsStored()
        {
            InMemoryConversationStore store = await SeededStoreAsync();
            ScriptedChatClient summariser = new("the summariser's gist");
            ConversationSession session = CreateSession(
                new ScriptedChatClient("a3"), store, conversationId: "c1",
                compaction: Summary(summariser, minimumPreservedGroups: 0),
                hooks: [new Gate(gate => gate.ReplaceSummary("the hook's summary"))]);

            _ = await session.RunTurnAsync("q3", Ct);
            await session.FlushTranscriptAsync();

            ConversationMessage summary = Assert.Single(
                await store.ReadForSessionAsync("c1", Ct), row => row.CoversUpTo is not null);
            Assert.Contains("the hook's summary", summary.Content.Text, StringComparison.Ordinal);
            Assert.DoesNotContain("the summariser's gist", summary.Content.Text, StringComparison.Ordinal);
            Assert.Equal(0, summariser.Calls);
        }

        // A gate that fails Closed applies its safe verb, and the compaction gate's safe verb is Cancel.
        [Fact]
        public async Task AClosedGateThatFailsCancelsTheCompaction()
        {
            InMemoryConversationStore store = await SeededStoreAsync();
            RecordingHook notices = new();
            ScriptedChatClient summariser = new("the gist");
            ConversationSession session = CreateSession(
                new ScriptedChatClient("a3"), store, conversationId: "c1",
                compaction: Summary(summariser, minimumPreservedGroups: 0),
                hooks: [new Throwing(HookFailure.Closed), notices]);

            _ = await session.RunTurnAsync("q3", Ct);
            await session.FlushTranscriptAsync();
            await session.FlushNoticesAsync();

            Assert.DoesNotContain(await store.ReadForSessionAsync("c1", Ct), row => row.CoversUpTo is not null);
            Assert.Equal(0, summariser.Calls);
            Assert.Equal(CompactionOutcome.Cancelled, Assert.Single(notices.Of<Compacted>()).Outcome);
        }

        // A gate that fails Open leaves the compaction as if the hook were not there.
        [Fact]
        public async Task AnOpenGateThatFailsLetsTheCompactionRun()
        {
            InMemoryConversationStore store = await SeededStoreAsync();
            RecordingHook notices = new();
            ConversationSession session = CreateSession(
                new ScriptedChatClient("a3"), store, conversationId: "c1",
                compaction: Summary(new ScriptedChatClient("the summariser's gist"), minimumPreservedGroups: 0),
                hooks: [new Throwing(HookFailure.Open), notices]);

            _ = await session.RunTurnAsync("q3", Ct);
            await session.FlushTranscriptAsync();
            await session.FlushNoticesAsync();

            ConversationMessage summary = Assert.Single(
                await store.ReadForSessionAsync("c1", Ct), row => row.CoversUpTo is not null);
            Assert.Contains("the summariser's gist", summary.Content.Text, StringComparison.Ordinal);
            Assert.Equal(CompactionOutcome.Compacted, Assert.Single(notices.Of<Compacted>()).Outcome);
        }

        // Nothing is compacted, and the turn stands.
        [Fact]
        public async Task ACancelledCompactionKeepsTheView()
        {
            InMemoryConversationStore store = await SeededStoreAsync();
            RecordingHook notices = new();
            ConversationSession session = CreateSession(
                new ScriptedChatClient("a3"), store, conversationId: "c1",
                compaction: Summary(new ScriptedChatClient("the gist"), minimumPreservedGroups: 0),
                hooks: [new Gate(gate => gate.Cancel()), notices]);

            TurnResult turn = await session.RunTurnAsync("q3", Ct);
            await session.FlushTranscriptAsync();
            await session.FlushNoticesAsync();

            Assert.Equal("a3", turn.ReplyText);
            Assert.DoesNotContain(await store.ReadForSessionAsync("c1", Ct), row => row.CoversUpTo is not null);
            Assert.Equal(CompactionOutcome.Cancelled, Assert.Single(notices.Of<Compacted>()).Outcome);
        }

        // A compaction is named, with how many messages it left. The gate counts the messages the strategy may fold: the stored turns before the newest one.
        [Fact]
        public async Task ACompactionIsNamedWithItsCounts()
        {
            InMemoryConversationStore store = await SeededStoreAsync();
            RecordingHook notices = new();
            List<int> counts = [];
            ConversationSession session = CreateSession(
                new ScriptedChatClient("a3"), store, conversationId: "c1",
                compaction: Summary(new ScriptedChatClient("the gist"), minimumPreservedGroups: 0),
                hooks: [new Gate(gate => counts.Add(gate.MessageCount)), notices]);

            _ = await session.RunTurnAsync("q3", Ct);
            await session.FlushNoticesAsync();

            Compacted compacted = Assert.Single(notices.Of<Compacted>());
            Assert.Equal(CompactionOutcome.Compacted, compacted.Outcome);
            // Before: six stored rows and the new question. After: one summary, the newest stored turn's two rows, the new question.
            Assert.Equal(7, compacted.MessagesBefore);
            Assert.Equal(4, compacted.MessagesAfter);
            Assert.Equal(4, Assert.Single(counts));
        }

        private sealed class Throwing(HookFailure policy) : AgentHook
        {
            public override HookFailure FailureFor(GatePoint point) => policy;

            public override ValueTask BeforeCompactionAsync(CompactionGate gate, CancellationToken cancellationToken)
            {
                throw new InvalidOperationException("the hook broke");
            }
        }

        private sealed class Gate(Action<CompactionGate> decide) : AgentHook
        {
            public override ValueTask BeforeCompactionAsync(CompactionGate gate, CancellationToken cancellationToken)
            {
                decide(gate);
                return default;
            }
        }
    }
}
