using AgentCore.Application.Conversation.Memory;
using AgentCore.TestSupport;
using Xunit;

namespace AgentCore.Application.Tests.Conversation;

/// <summary>The summary row facts of <see cref="ConversationStoreSummaryRowFacts"/>, against the in-memory store.</summary>
public sealed class InMemoryConversationStoreSummaryRowTests
{
    [Fact]
    public Task ReadForSession_HandsBackTheNewestSummaryAndTheRowsAboveWhatItCovers()
        => ConversationStoreSummaryRowFacts.SessionReadHandsBackTheNewestSummaryAndTheRowsAboveWhatItCovers(new InMemoryConversationStore(), TestContext.Current.CancellationToken);

    [Fact]
    public Task ReadForSession_WithNoSummary_HandsBackEveryRow()
        => ConversationStoreSummaryRowFacts.SessionReadWithNoSummaryHandsBackEveryRow(new InMemoryConversationStore(), TestContext.Current.CancellationToken);

    [Fact]
    public Task ConsumerReads_LeaveEverySummaryOut()
        => ConversationStoreSummaryRowFacts.ConsumerReadsLeaveEverySummaryOut(new InMemoryConversationStore(), TestContext.Current.CancellationToken);

    [Fact]
    public Task Truncate_UnderWhatTheSummaryCovers_TakesTheSummary()
        => ConversationStoreSummaryRowFacts.TruncateUnderWhatTheSummaryCoversTakesTheSummary(new InMemoryConversationStore(), TestContext.Current.CancellationToken);

    [Fact]
    public Task Truncate_AboveWhatTheSummaryCovers_KeepsTheSummaryWhateverItsOrdinal()
        => ConversationStoreSummaryRowFacts.TruncateAboveWhatTheSummaryCoversKeepsTheSummaryWhateverItsOrdinal(new InMemoryConversationStore(), TestContext.Current.CancellationToken);

    [Fact]
    public Task Truncate_AboveTheSummary_LeavesEverythingUnderItAlone()
        => ConversationStoreSummaryRowFacts.TruncateAboveTheSummaryLeavesEverythingUnderItAlone(new InMemoryConversationStore(), TestContext.Current.CancellationToken);

    [Fact]
    public Task OrdinalOf_FindsASpokenRow_AndNotASummary()
        => ConversationStoreSummaryRowFacts.OrdinalOfFindsASpokenRowAndNotASummary(new InMemoryConversationStore(), TestContext.Current.CancellationToken);
}
