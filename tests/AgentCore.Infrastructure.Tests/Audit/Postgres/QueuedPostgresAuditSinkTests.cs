using AgentCore.Application.Audit;
using AgentCore.Domain.Audit;
using AgentCore.Infrastructure.Audit.Postgres;
using AgentCore.Infrastructure.Tests.Database.Postgres;
using AgentCore.TestSupport;
using Npgsql;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Audit.Postgres
{
    /// <summary>
    /// The audit queue retrying into the PostgreSQL store.
    /// </summary>
    public sealed class QueuedPostgresAuditSinkTests : PostgresDatabaseTest
    {
        /// <inheritdoc />
        protected override bool Migrated => true;

        [PostgresFact]
        public async Task ABatchThatCommittedButReportedATransientFault_IsRetriedAndWrittenOnce_AndTheChainStaysDense()
        {
            FakeTimeProvider time = new(DateTimeOffset.UnixEpoch);
            LostReplyAuditSink store = new(new PostgresAuditSink(DataSource));
            await using QueuedAuditSink sink = new(store, timeProvider: time);
            AuditEvent first = Event();
            AuditEvent second = Event();
            AuditEvent later = Event();

            await sink.AppendAsync(first, Token);
            await sink.AppendAsync(second, Token);
            await store.LostReply.WaitAsync(TimeSpan.FromSeconds(10), Token);
            await time.WaitForTimersAsync(1).WaitAsync(TimeSpan.FromSeconds(10), Token);
            await sink.AppendAsync(later, Token);

            time.Advance(TimeSpan.FromMinutes(1));
            await sink.FlushAsync(Token);

            // The replay reached a table that already held the first two rows. The (conversation_id, event_id)
            // key and the NOT EXISTS filter wrote nothing twice and left no hole in the sequence.
            Assert.Equal(
                [(first.EventId, 0L), (second.EventId, 1L), (later.EventId, 2L)],
                await RowsOfAsync("C1"));
        }

        private static AuditEvent Event()
        {
            return new()
            {
                ConversationId = "C1",
                EventId = Guid.CreateVersion7(),
                Kind = AuditEventKind.ConversationStarted,
                OccurredAt = DateTimeOffset.UnixEpoch,
            };
        }

        private async Task<List<(Guid EventId, long Sequence)>> RowsOfAsync(string conversationId)
        {
            await using NpgsqlCommand command = DataSource.CreateCommand(
                "SELECT event_id, sequence FROM agentcore.audit_event WHERE conversation_id = $1 ORDER BY sequence");
            _ = command.Parameters.Add(new NpgsqlParameter { Value = conversationId });

            List<(Guid, long)> rows = [];
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(Token);
            while (await reader.ReadAsync(Token))
            {
                rows.Add((reader.GetGuid(0), reader.GetInt64(1)));
            }

            return rows;
        }
    }
}
