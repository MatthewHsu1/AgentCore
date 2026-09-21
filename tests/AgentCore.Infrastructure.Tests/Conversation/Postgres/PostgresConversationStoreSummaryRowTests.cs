using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Conversation.Postgres;
using AgentCore.Infrastructure.Tests.Database.Postgres;
using AgentCore.TestSupport;

namespace AgentCore.Infrastructure.Tests.Conversation.Postgres
{
    /// <summary>The summary row facts of <see cref="ConversationStoreSummaryRowFacts"/>, against PostgreSQL.</summary>
    public sealed class PostgresConversationStoreSummaryRowTests : PostgresDatabaseTest
    {
        protected override bool Migrated => true;

        private IConversationStore Store => new PostgresConversationStore(DataSource);

        [PostgresFact]
        public Task ReadForSession_HandsBackTheNewestSummaryAndTheRowsAboveWhatItCovers()
        {
            return ConversationStoreSummaryRowFacts.SessionReadHandsBackTheNewestSummaryAndTheRowsAboveWhatItCovers(Store, Token);
        }

        [PostgresFact]
        public Task ReadForSession_WithNoSummary_HandsBackEveryRow()
        {
            return ConversationStoreSummaryRowFacts.SessionReadWithNoSummaryHandsBackEveryRow(Store, Token);
        }

        [PostgresFact]
        public Task ConsumerReads_LeaveEverySummaryOut()
        {
            return ConversationStoreSummaryRowFacts.ConsumerReadsLeaveEverySummaryOut(Store, Token);
        }

        [PostgresFact]
        public Task Truncate_UnderWhatTheSummaryCovers_TakesTheSummary()
        {
            return ConversationStoreSummaryRowFacts.TruncateUnderWhatTheSummaryCoversTakesTheSummary(Store, Token);
        }

        [PostgresFact]
        public Task Truncate_AboveWhatTheSummaryCovers_KeepsTheSummaryWhateverItsOrdinal()
        {
            return ConversationStoreSummaryRowFacts.TruncateAboveWhatTheSummaryCoversKeepsTheSummaryWhateverItsOrdinal(Store, Token);
        }

        [PostgresFact]
        public Task Truncate_AboveTheSummary_LeavesEverythingUnderItAlone()
        {
            return ConversationStoreSummaryRowFacts.TruncateAboveTheSummaryLeavesEverythingUnderItAlone(Store, Token);
        }

        [PostgresFact]
        public Task OrdinalOf_FindsASpokenRow_AndNotASummary()
        {
            return ConversationStoreSummaryRowFacts.OrdinalOfFindsASpokenRowAndNotASummary(Store, Token);
        }
    }
}
