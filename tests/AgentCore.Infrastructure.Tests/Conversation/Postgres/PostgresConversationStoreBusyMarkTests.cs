using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Conversation.Postgres;
using AgentCore.Infrastructure.Tests.Database.Postgres;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Conversation.Postgres
{
    /// <summary>
    /// The busy mark a running turn puts on its conversation, in PostgreSQL: one holder at a time, renewed by the same
    /// holder, freed by a clear or by its lease lapsing.
    /// </summary>
#pragma warning disable CA1859 // store is typed as IConversationStore throughout, as every caller holds it.
    public sealed class PostgresConversationStoreBusyMarkTests : PostgresDatabaseTest
    {
        private static readonly TimeSpan Lease = TimeSpan.FromMinutes(1);

        /// <inheritdoc />
        protected override bool Migrated => true;

        [PostgresFact]
        public async Task TryMarkBusyAsync_ALiveMarkOfAnotherHolder_IsRefused_AndItsHolderRenewsIt()
        {
            // Arrange
            IConversationStore store = new PostgresConversationStore(DataSource);

            // Act
            bool first = await store.TryMarkBusyAsync("c1", "host-a", Lease, Token);
            bool other = await store.TryMarkBusyAsync("c1", "host-b", Lease, Token);
            bool renewed = await store.TryMarkBusyAsync("c1", "host-a", Lease, Token);

            // Assert
            Assert.Equal((true, false, true), (first, other, renewed));
            Assert.Equal("host-a", await ScalarAsync<string>("SELECT holder FROM agentcore.conversation_busy WHERE conversation_id = 'c1'"));
        }

        [PostgresFact]
        public async Task ClearBusyAsync_ByAnotherHolder_LeavesTheMark_AndByItsHolder_FreesIt()
        {
            // Arrange
            IConversationStore store = new PostgresConversationStore(DataSource);
            _ = await store.TryMarkBusyAsync("c1", "host-a", Lease, Token);

            // Act
            await store.ClearBusyAsync("c1", "host-b", Token);
            bool whileMarked = await store.TryMarkBusyAsync("c1", "host-b", Lease, Token);
            await store.ClearBusyAsync("c1", "host-a", Token);
            bool afterClear = await store.TryMarkBusyAsync("c1", "host-b", Lease, Token);

            // Assert
            Assert.Equal((false, true), (whileMarked, afterClear));
        }

        [PostgresFact]
        public async Task TryMarkBusyAsync_AMarkWhoseLeaseLapsed_IsTakenByAnotherHolder()
        {
            // Arrange: a host that crashed leaves a mark nobody renews, which has lapsed by now.
            IConversationStore store = new PostgresConversationStore(DataSource);
            _ = await store.TryMarkBusyAsync("c1", "crashed", TimeSpan.FromSeconds(-1), Token);

            // Act
            bool taken = await store.TryMarkBusyAsync("c1", "host-b", Lease, Token);

            // Assert
            Assert.True(taken);
        }

        [PostgresFact]
        public async Task TryMarkBusyAsync_AConversationWithNoRowYet_IsMarked_AndGetsNoRow()
        {
            // Arrange
            IConversationStore store = new PostgresConversationStore(DataSource);

            // Act
            bool marked = await store.TryMarkBusyAsync("new", "host-a", Lease, Token);

            // Assert
            Assert.True(marked);
            Assert.Null(await store.GetAsync("new", Token));
        }
    }
#pragma warning restore CA1859
}
