using AgentCore.Application.Conversation.Memory;
using AgentCore.Application.Runtime.Compaction;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tests.Transcript;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Xunit;
using AgentCore.Application.Runtime.Session;
using static AgentCore.Application.Tests.Transcript.ConversationSessionCompactionTestSupport;

namespace AgentCore.Application.Tests.Runtime.Compaction
{
    /// <summary>
    /// A start notice goes out only when the summariser model is actually called, and an end
    /// notice always follows it with the outcome the pass had. Every fact here comes from a real
    /// streaming turn, so the notice count is read off the wire the caller would see, not off an
    /// internal hook.
    /// </summary>
#pragma warning disable MAAI001 // Compaction is evaluation-only in Microsoft.Agents.AI 1.21.0.
    public sealed class SummaryRowProviderNoticeTests
    {
        [Fact]
        public async Task ATriggerThatNeverFires_CallsTheSummariserZeroTimes_AndPostsNoNotice()
        {
            InMemoryConversationStore store = await SeededStoreAsync();
            CapturingChatClient summariser = new(_ => Task.CompletedTask, "unused");
            ConversationSession session = CreateSession(
                new ScriptedChatClient("a3"),
                store,
                conversationId: "c1",
                compaction: SummaryOnly(summariser, client => new SummarizationCompactionStrategy(client, CompactionTriggers.Never, minimumPreservedGroups: 0)));

            List<ChatResponseUpdate> updates = await RunAsync(session);

            Assert.Empty(summariser.Requests);
            Assert.Empty(NoticesIn(updates));
        }

        [Fact]
        public async Task ATriggerThatAlwaysFires_CallsTheSummariserOnce_AndPostsStartThenEndCompacted()
        {
            InMemoryConversationStore store = await SeededStoreAsync();
            CapturingChatClient summariser = new(_ => Task.CompletedTask, "the gist of it");
            ConversationSession session = CreateSession(
                new ScriptedChatClient("a3"),
                store,
                conversationId: "c1",
                compaction: Summary(summariser, minimumPreservedGroups: 0));

            List<ChatResponseUpdate> updates = await RunAsync(session);

            _ = Assert.Single(summariser.Requests);
            Assert.Equal([(CompactionContent.StartPhase, null), (CompactionContent.EndPhase, "compacted")], NoticesIn(updates));
        }

        [Fact]
        public async Task ASummariserThatThrows_PostsStartThenEndUnchanged_AndTheReplyStillRuns()
        {
            // SummarizationCompactionStrategy swallows its own client's exception and reports no
            // compaction, so the provider sees a called summariser and an untouched view: "unchanged",
            // not "failed".
            InMemoryConversationStore store = await SeededStoreAsync();
            CapturingChatClient summariser = new(_ => Task.FromException(new IOException("summariser down")), "unused");
            ConversationSession session = CreateSession(
                new ScriptedChatClient("a3"),
                store,
                conversationId: "c1",
                compaction: Summary(summariser, minimumPreservedGroups: 0));

            List<ChatResponseUpdate> updates = await RunAsync(session);

            _ = Assert.Single(summariser.Requests);
            Assert.Equal([(CompactionContent.StartPhase, null), (CompactionContent.EndPhase, "unchanged")], NoticesIn(updates));
            Assert.Equal("a3", ReplyIn(updates));
        }

        [Fact]
        public async Task AStrategyThatThrowsAfterTheCall_PostsStartThenEndFailed_AndTheReplyStillRuns()
        {
            // No shipped strategy throws past its own guard; this one does, to reach the provider's
            // catch and prove the rule it exists for: a failed pass never ends the turn.
            InMemoryConversationStore store = await SeededStoreAsync();
            ConversationSession session = CreateSession(
                new ScriptedChatClient("a3"),
                store,
                conversationId: "c1",
                compaction: SummaryOnly(new ScriptedChatClient("unused"), client => new CallsThenThrowsStrategy(client)));

            List<ChatResponseUpdate> updates = await RunAsync(session);

            Assert.Equal([(CompactionContent.StartPhase, null), (CompactionContent.EndPhase, "failed")], NoticesIn(updates));
            Assert.Equal("a3", ReplyIn(updates));
        }

        private static async Task<List<ChatResponseUpdate>> RunAsync(ConversationSession session)
        {
            List<ChatResponseUpdate> updates = [];
            await foreach (ChatResponseUpdate update in session.RunTurnStreamingAsync("q3", TestContext.Current.CancellationToken))
            {
                updates.Add(update);
            }

            return updates;
        }

        private static IEnumerable<(string Phase, string? Outcome)> NoticesIn(IEnumerable<ChatResponseUpdate> updates)
        {
            return updates
                .SelectMany(update => update.Contents.OfType<CompactionContent>())
                .Select(notice => (notice.Phase, notice.Outcome));
        }

        private static string ReplyIn(IEnumerable<ChatResponseUpdate> updates)
        {
            return string.Concat(updates.Where(update => !update.Contents.OfType<CompactionContent>().Any()).Select(update => update.Text));
        }

        private sealed class CallsThenThrowsStrategy(IChatClient client) : CompactionStrategy(CompactionTriggers.Always)
        {
            protected override async ValueTask<bool> CompactCoreAsync(CompactionMessageIndex index, ILogger logger, CancellationToken cancellationToken)
            {
                _ = await client.GetResponseAsync([], cancellationToken: cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException("boom");
            }
        }
    }
#pragma warning restore MAAI001
}
