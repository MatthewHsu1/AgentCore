using AgentCore.Application.Conversation.Memory;
using AgentCore.Application.Runtime;
using AgentCore.Application.Runtime.Compaction;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tests.Transcript;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Xunit;
using static AgentCore.Application.Tests.Transcript.ConversationSessionCompactionTestSupport;

namespace AgentCore.Application.Tests.Runtime.Compaction
{
    /// <summary>
    /// D6: the streaming merge surfaces a compaction notice while the summariser's own model call is
    /// still in flight, in order, and a cancel mid-wait ends the stream rather than hanging it. The
    /// summariser is gated rather than delayed, so "the notice arrived before the summariser finished"
    /// is a fact the test controls directly instead of a race against a clock.
    /// </summary>
#pragma warning disable MAAI001 // Compaction is evaluation-only in Microsoft.Agents.AI 1.21.0.
    public sealed class ConversationTurnStreamCompactionNoticeTests
    {
        [Fact]
        public async Task TheStartNotice_ArrivesFirst_WhileTheSummariserIsStillBlocked()
        {
            InMemoryConversationStore store = await SeededStoreAsync();

            TaskCompletionSource reachedSummariser = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource releaseSummariser = new(TaskCreationOptions.RunContinuationsAsynchronously);
            CapturingChatClient summariser = new(
                async callIndex =>
                {
                    _ = reachedSummariser.TrySetResult();
                    await releaseSummariser.Task;
                },
                "the gist of it");

            ConversationSession session = CreateSession(
                new ScriptedChatClient("a3"),
                store,
                conversationId: "c1",
                compaction: Summary(summariser, minimumPreservedGroups: 0));

            IAsyncEnumerator<ChatResponseUpdate> stream = session
                .RunTurnStreamingAsync("q3", TestContext.Current.CancellationToken)
                .GetAsyncEnumerator(TestContext.Current.CancellationToken);
            await using (stream)
            {
                // The first update the caller sees is the start notice, and it arrives while the
                // summariser is still blocked on its own gate — proof the merge does not wait for the
                // provider's model call before surfacing what it already posted.
                Assert.True(await stream.MoveNextAsync());
                CompactionContent start = Assert.Single(stream.Current.Contents.OfType<CompactionContent>());
                Assert.Equal(CompactionContent.StartPhase, start.Phase);

                // The summariser's own model call is still blocked on its gate at this point: the
                // start notice above did not wait for it to finish, only for it to begin.
                await reachedSummariser.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                _ = releaseSummariser.TrySetResult();

                Assert.True(await stream.MoveNextAsync());
                CompactionContent end = Assert.Single(stream.Current.Contents.OfType<CompactionContent>());
                Assert.Equal(CompactionContent.EndPhase, end.Phase);
                Assert.Equal("compacted", end.Outcome);

                Assert.True(await stream.MoveNextAsync());
                Assert.Equal("a3", stream.Current.Text);
            }
        }

        [Fact]
        public async Task ACancelWhileTheSummariserIsBlocked_EndsTheStreamWithOperationCanceled_AndDoesNotHang()
        {
            InMemoryConversationStore store = await SeededStoreAsync();

            GatedChatClient summariser = new();

            ConversationSession session = CreateSession(
                new ScriptedChatClient("a3"),
                store,
                conversationId: "c1",
                compaction: Summary(summariser, minimumPreservedGroups: 0));

            using CancellationTokenSource cts = new();
            List<string> seen = [];

            Task run = Task.Run(async () =>
            {
                await foreach (ChatResponseUpdate update in session.RunTurnStreamingAsync("q3", cts.Token))
                {
                    seen.Add(update.Contents.OfType<CompactionContent>().FirstOrDefault()?.Phase ?? "text");
                    if (seen.Count == 1)
                    {
                        cts.Cancel();
                    }
                }
            }, TestContext.Current.CancellationToken);

            Task finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

            Assert.Same(run, finished);
            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            Assert.Equal([CompactionContent.StartPhase], seen);
        }

        /// <summary>
        /// A summariser that blocks until the run's own cancellation token fires, the way a real HTTP
        /// call would. <see cref="CapturingChatClient"/>'s hook carries no token to block on, so it
        /// cannot stand in for a provider that actually respects cancellation.
        /// </summary>
        private sealed class GatedChatClient : IChatClient
        {
            public async Task<ChatResponse> GetResponseAsync(
                IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, "unreachable"));
            }

            public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException("Summarisation runs the buffered path only.");
            }

            public object? GetService(Type serviceType, object? serviceKey = null) => null;

            public void Dispose()
            {
            }
        }
    }
#pragma warning restore MAAI001
}
