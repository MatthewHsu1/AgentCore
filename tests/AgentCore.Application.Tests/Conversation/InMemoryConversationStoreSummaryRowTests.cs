using AgentCore.Application.Conversation.Memory;
using AgentCore.TestSupport;
using Xunit;

namespace AgentCore.Application.Tests.Conversation
{
    /// <summary>The summary row facts of <see cref="ConversationStoreSummaryRowFacts"/>, against the in-memory store.</summary>
    public sealed class InMemoryConversationStoreSummaryRowTests
    {
        [Fact]
        public Task ReadForSession_HandsBackTheNewestSummaryAndTheRowsAboveWhatItCovers()
        {
            return ConversationStoreSummaryRowFacts.SessionReadHandsBackTheNewestSummaryAndTheRowsAboveWhatItCovers(new InMemoryConversationStore(), TestContext.Current.CancellationToken);
        }

        [Fact]
        public Task ReadForSession_WithNoSummary_HandsBackEveryRow()
        {
            return ConversationStoreSummaryRowFacts.SessionReadWithNoSummaryHandsBackEveryRow(new InMemoryConversationStore(), TestContext.Current.CancellationToken);
        }

        [Fact]
        public Task ConsumerReads_LeaveEverySummaryOut()
        {
            return ConversationStoreSummaryRowFacts.ConsumerReadsLeaveEverySummaryOut(new InMemoryConversationStore(), TestContext.Current.CancellationToken);
        }

        [Fact]
        public Task Truncate_UnderWhatTheSummaryCovers_TakesTheSummary()
        {
            return ConversationStoreSummaryRowFacts.TruncateUnderWhatTheSummaryCoversTakesTheSummary(new InMemoryConversationStore(), TestContext.Current.CancellationToken);
        }

        [Fact]
        public Task Truncate_AboveWhatTheSummaryCovers_KeepsTheSummaryWhateverItsOrdinal()
        {
            return ConversationStoreSummaryRowFacts.TruncateAboveWhatTheSummaryCoversKeepsTheSummaryWhateverItsOrdinal(new InMemoryConversationStore(), TestContext.Current.CancellationToken);
        }

        [Fact]
        public Task Truncate_AboveTheSummary_LeavesEverythingUnderItAlone()
        {
            return ConversationStoreSummaryRowFacts.TruncateAboveTheSummaryLeavesEverythingUnderItAlone(new InMemoryConversationStore(), TestContext.Current.CancellationToken);
        }

        [Fact]
        public Task OrdinalOf_FindsASpokenRow_AndNotASummary()
        {
            return ConversationStoreSummaryRowFacts.OrdinalOfFindsASpokenRowAndNotASummary(new InMemoryConversationStore(), TestContext.Current.CancellationToken);
        }
    }
}
