using AgentCore.Application.Conversation;
using AgentCore.Application.Conversation.Memory;
using AgentCore.Application.Hooks.Notices;
using AgentCore.Application.Ports;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Transcript;
using AgentCore.Domain;
using AgentCore.TestSupport;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Xunit;
using AgentCore.Application.Runtime.Session;
using static AgentCore.Application.Tests.Transcript.ConversationSessionCompactionTestSupport;

namespace AgentCore.Application.Tests.Transcript
{
    /// <summary>
    /// Compaction that goes wrong costs the conversation nothing: a strategy that throws, one whose
    /// output is not one summary, a refused row write, and words that move under the summariser.
    /// </summary>
#pragma warning disable MAAI001 // Compaction is evaluation-only in Microsoft.Agents.AI 1.21.0.
    public sealed class ConversationSessionCompactionFailureTests
    {
        [Fact]
        public async Task CompactAsync_AStrategyThatThrows_LeavesTheTurnAndTheStoreUntouched_AndLogsEvent26()
        {
            InMemoryConversationStore store = new();
            _ = await store.CreateAsync("c1", TestContext.Current.CancellationToken);
            await SeedPlainTurnAsync(store, turnIndex: 0, "q0", "a0");
            await SeedPlainTurnAsync(store, turnIndex: 1, "q1", "a1");

            RecordingLoggerFactory loggerFactory = new();
            ConversationSession session = CreateSession(
                new ScriptedChatClient("a2"), store, conversationId: "c1", logger: loggerFactory.CreateLogger("test"),
                compaction: SummaryOnly(new ThrowingCompactionStrategy()));

            TurnResult result = await session.RunTurnAsync("q2", TestContext.Current.CancellationToken);
            await session.FlushTranscriptAsync();

            Assert.Null(result.Failure);
            Assert.Equal("a2", result.ReplyText);
            Assert.DoesNotContain(await store.ReadForSessionAsync("c1", TestContext.Current.CancellationToken), row => row.CoversUpTo is not null);
            _ = Assert.Single(loggerFactory.Of(26));
        }

        [Fact]
        public async Task CompactAsync_AStrategyWhoseOutputIsNotOneSummary_KeepsTheView_AndLogsEvent27()
        {
            InMemoryConversationStore store = new();
            _ = await store.CreateAsync("c1", TestContext.Current.CancellationToken);
            await SeedPlainTurnAsync(store, turnIndex: 0, "q0", "a0");
            await SeedPlainTurnAsync(store, turnIndex: 1, "q1", "a1");

            RecordingLoggerFactory loggerFactory = new();
            CapturingChatClient reply = new(_ => Task.CompletedTask, "a2");
            ConversationSession session = CreateSession(
                reply, store, conversationId: "c1", logger: loggerFactory.CreateLogger("test"),
                compaction: SummaryOnly(new TruncatingCompactionStrategy()));

            _ = await session.RunTurnAsync("q2", TestContext.Current.CancellationToken);
            await session.FlushTranscriptAsync();

            Assert.Equal(["user:q0", "assistant:a0", "user:q1", "assistant:a1", "user:q2"], Assert.Single(reply.Requests));
            Assert.DoesNotContain(await store.ReadForSessionAsync("c1", TestContext.Current.CancellationToken), row => row.CoversUpTo is not null);
            _ = Assert.Single(loggerFactory.Of(27));
        }

        [Fact]
        public async Task CompactAsync_TheRowWriteFails_IsReportedAsADroppedWriteAndTheTurnStands()
        {
            RefusingSummaryWrite store = new(new InMemoryConversationStore());
            _ = await store.CreateAsync("c1", TestContext.Current.CancellationToken);
            await SeedPlainTurnAsync(store, turnIndex: 0, "q0", "a0");
            await SeedPlainTurnAsync(store, turnIndex: 1, "q1", "a1");
            await SeedPlainTurnAsync(store, turnIndex: 2, "q2", "a2");

            RecordingHook hook = new();
            ConversationSession session = CreateSession(
                new ScriptedChatClient("a3"), store, conversationId: "c1",
                compaction: Summary(new ScriptedChatClient("the gist of it"), minimumPreservedGroups: 0),
                hooks: [hook]);

            TurnResult result = await session.RunTurnAsync("q3", TestContext.Current.CancellationToken);
            await session.FlushTranscriptAsync();

            Assert.Equal("a3", result.ReplyText);
            await session.FlushNoticesAsync();
            Fault dropped = Assert.Single(hook.Of<Fault>(), fault => fault.Kind == FaultKind.TranscriptWriteFailed);
            Assert.Contains("summary row refused", dropped.Message, StringComparison.Ordinal);

            // The words landed; only the summary did not.
            Assert.DoesNotContain(await store.ReadForSessionAsync("c1", TestContext.Current.CancellationToken), row => row.CoversUpTo is not null);
            Assert.Equal(8, (await store.ReadAllAsync("c1", TestContext.Current.CancellationToken)).Count);
        }

        [Fact]
        public async Task CompactAsync_WordsMoveWhileTheSummariserRuns_TheSummaryIsRefused()
        {
            InMemoryConversationStore store = new();
            _ = await store.CreateAsync("c1", TestContext.Current.CancellationToken);
            await SeedPlainTurnAsync(store, turnIndex: 0, "q0", "a0");
            await SeedPlainTurnAsync(store, turnIndex: 1, "q1", "a1");
            await SeedPlainTurnAsync(store, turnIndex: 2, "q2", "a2");

            ConversationSession? session = null;
            BargingCompactionStrategy strategy = new(
                new SummarizationCompactionStrategy(new ScriptedChatClient("the gist of it"), CompactionTriggers.Always, minimumPreservedGroups: 0),
                () => _ = session!.Compiled.History.CommitTurn(session.AgentSession!, new TurnCommit(new ChatMessage(ChatRole.User, "barged in"))));
            session = CreateSession(new ScriptedChatClient("a3"), store, conversationId: "c1", compaction: SummaryOnly(strategy));

            TurnResult result = await session.RunTurnAsync("q3", TestContext.Current.CancellationToken);
            await session.FlushTranscriptAsync();

            Assert.Equal("a3", result.ReplyText);
            Assert.DoesNotContain(await store.ReadForSessionAsync("c1", TestContext.Current.CancellationToken), row => row.CoversUpTo is not null);
            Assert.Null(session.Compiled.History.Summary(session.AgentSession!));
            Assert.Contains(await store.ReadAllAsync("c1", TestContext.Current.CancellationToken), row => row.Content.Text == "barged in");
        }

        /// <summary>A store that refuses to write a summary row and takes every other row.</summary>
        private sealed class RefusingSummaryWrite(IConversationStore inner) : DelegatingConversationStore(inner)
        {
            public override ValueTask<IReadOnlyList<ConversationMessage>> AppendAsync(
                string conversationId, IReadOnlyList<ConversationMessageDraft> messages, ConversationSessionState? state = null, CancellationToken cancellationToken = default)
            {
                return messages.Any(draft => draft.CoversUpTo is not null)
                                ? throw new InvalidOperationException("summary row refused")
                                : base.AppendAsync(conversationId, messages, state, cancellationToken);
            }
        }

        /// <summary>A strategy that always fires and always throws, to prove a failed compaction costs the turn nothing.</summary>
        private sealed class ThrowingCompactionStrategy() : CompactionStrategy(CompactionTriggers.Always)
        {
            protected override ValueTask<bool> CompactCoreAsync(CompactionMessageIndex index, ILogger logger, CancellationToken cancellationToken)
            {
                throw new InvalidOperationException("the summariser is down");
            }
        }

        /// <summary>Drops the oldest group and inserts nothing: a shape no summary row can stand for.</summary>
        private sealed class TruncatingCompactionStrategy() : CompactionStrategy(CompactionTriggers.Always)
        {
            protected override ValueTask<bool> CompactCoreAsync(CompactionMessageIndex index, ILogger logger, CancellationToken cancellationToken)
            {
                index.Groups[0].IsExcluded = true;
                return new(true);
            }
        }

        /// <summary>Moves the words before forwarding to <paramref name="inner"/>, as a writer racing the compaction would.</summary>
        private sealed class BargingCompactionStrategy(CompactionStrategy inner, Action bargeIn) : CompactionStrategy(CompactionTriggers.Always)
        {
            protected override ValueTask<bool> CompactCoreAsync(CompactionMessageIndex index, ILogger logger, CancellationToken cancellationToken)
            {
                bargeIn();
                return inner.CompactAsync(index, logger, cancellationToken);
            }
        }
    }
#pragma warning restore MAAI001
}
