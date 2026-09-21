using AgentCore.Application.Conversation.Memory;
using AgentCore.Application.Runtime;
using AgentCore.Application.Runtime.Compaction;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Transcript;
using AgentCore.TestSupport;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Xunit;
using static AgentCore.Application.Tests.Transcript.ConversationSessionCompactionTestSupport;

namespace AgentCore.Application.Tests.Transcript
{
    /// <summary>
    /// Compaction driven end to end through a real session: the summary a real MAF strategy produces
    /// lands as one row before the firing turn's own model call, is read back by the next session,
    /// survives a barge-in, and a strategy that throws costs the conversation nothing.
    /// </summary>
#pragma warning disable MAAI001 // Compaction is evaluation-only in Microsoft.Agents.AI 1.21.0.
    public sealed class ConversationSessionCompactionTests
    {
        [Fact]
        public async Task TheFiringTurn_CallsTheSummariserOnce_AndSendsTheSummaryInItsOwnRequest()
        {
            InMemoryConversationStore store = new();
            _ = await store.CreateAsync("c1", TestContext.Current.CancellationToken);
            await SeedPlainTurnAsync(store, turnIndex: 0, "q0", "a0");
            await SeedPlainTurnAsync(store, turnIndex: 1, "q1", "a1");
            await SeedPlainTurnAsync(store, turnIndex: 2, "q2", "a2");

            CapturingChatClient summariser = new(_ => Task.CompletedTask, "the gist of it");
            CapturingChatClient reply = new(_ => Task.CompletedTask, "a3");
            ConversationSession session = CreateSession(reply, store, conversationId: "c1", compaction: Summary(summariser, minimumPreservedGroups: 0));

            _ = await session.RunTurnAsync("q3", TestContext.Current.CancellationToken);
            await session.FlushTranscriptAsync();

            _ = Assert.Single(summariser.Requests);

            // Turn 2 (q2/a2) is the newest existing turn and stays live (D11); turns 0 and 1 go under the summary.
            Assert.Equal(["assistant:[Summary]\nthe gist of it", "user:q2", "assistant:a2", "user:q3"], Assert.Single(reply.Requests));

            IReadOnlyList<ConversationMessage> forSession = await store.ReadForSessionAsync("c1", TestContext.Current.CancellationToken);
            ConversationMessage summary = Assert.Single(forSession, row => row.CoversUpTo is not null);
            Assert.Equal((6, 3, 3), (summary.Ordinal, summary.CoversUpTo, summary.TurnIndex));
            Assert.Equal([4, 5, 6, 7, 8], forSession.Select(row => row.Ordinal));
        }

        [Fact]
        public async Task ASecondSummary_FoldsTheFirstIntoItself_AndOneRowStands()
        {
            InMemoryConversationStore store = new();
            _ = await store.CreateAsync("c1", TestContext.Current.CancellationToken);
            await SeedPlainTurnAsync(store, turnIndex: 0, "q0", "a0");
            await SeedPlainTurnAsync(store, turnIndex: 1, "q1", "a1");
            await SeedPlainTurnAsync(store, turnIndex: 2, "q2", "a2");

            CapturingChatClient summariser = new(_ => Task.CompletedTask, "SUMMARY-ONE", "SUMMARY-TWO");
            CapturingChatClient reply = new(_ => Task.CompletedTask, "a3", "a4");
            ConversationSession session = CreateSession(reply, store, conversationId: "c1", compaction: Summary(summariser, minimumPreservedGroups: 0));

            _ = await session.RunTurnAsync("q3", TestContext.Current.CancellationToken);
            _ = await session.RunTurnAsync("q4", TestContext.Current.CancellationToken);
            await session.FlushTranscriptAsync();

            // The second summariser call read the first summary, and the rows above it that came under the floor.
            Assert.Equal(2, summariser.Requests.Count);
            Assert.Equal(["assistant:[Summary]\nSUMMARY-ONE", "user:q2", "assistant:a2"], summariser.Requests[1].Skip(1));
            Assert.Equal(["assistant:[Summary]\nSUMMARY-TWO", "user:q3", "assistant:a3", "user:q4"], reply.Requests[1]);

            IReadOnlyList<ConversationMessage> forSession = await store.ReadForSessionAsync("c1", TestContext.Current.CancellationToken);
            ConversationMessage summary = Assert.Single(forSession, row => row.CoversUpTo is not null);
            Assert.Equal((9, 5), (summary.Ordinal, summary.CoversUpTo));
            Assert.Contains("SUMMARY-TWO", summary.Content.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(forSession, row => row.Content.Text.Contains("SUMMARY-ONE", StringComparison.Ordinal));
        }

        [Fact]
        public async Task TheSummary_ReadsTheCappedResults_AndCoversTheirRows()
        {
            InMemoryConversationStore store = new();
            _ = await store.CreateAsync("c1", TestContext.Current.CancellationToken);
            await SeedToolTurnAsync(store, turnIndex: 0, "q0", "toolA", "call-0", "result-A", "a0");
            await SeedToolTurnAsync(store, turnIndex: 1, "q1", "toolB", "call-1", "result-B", "a1");
            await SeedPlainTurnAsync(store, turnIndex: 2, "q2", "a2");

            // Cap every result to 3 characters; summarise all but the newest four groups, which are
            // q1 (call-1 + result-1) a1: the summary takes q0, its tool group and a0, rows 0-3.
            CapturingChatClient summariser = new(_ => Task.CompletedTask, "the gist of it");
            CompactionStages stages = new(
                new ToolResultCapProvider(CompactionTriggers.Always, keepTurns: 0, maxResultChars: 3),
                summariser,
                client => new SummarizationCompactionStrategy(client, CompactionTriggers.Always, minimumPreservedGroups: 3));
            ConversationSession session = CreateSession(new ScriptedChatClient("a3"), store, conversationId: "c1", compaction: stages);

            _ = await session.RunTurnAsync("q3", TestContext.Current.CancellationToken);
            await session.FlushTranscriptAsync();

            List<string> request = Assert.Single(summariser.Requests);
            Assert.Contains("tool:res…", request);
            Assert.DoesNotContain("tool:result-A", request);

            ConversationMessage summary = Assert.Single(await store.ReadForSessionAsync("c1", TestContext.Current.CancellationToken), row => row.CoversUpTo is not null);
            Assert.Equal(3, summary.CoversUpTo);
        }

        [Fact]
        public async Task ACapOnlyStage_WritesNoRow_AndTheRequestCarriesTheCappedResults()
        {
            InMemoryConversationStore store = new();
            _ = await store.CreateAsync("c1", TestContext.Current.CancellationToken);
            await SeedToolTurnAsync(store, turnIndex: 0, "q0", "toolA", "call-0", "result-A", "a0");
            await SeedPlainTurnAsync(store, turnIndex: 1, "q1", "a1");

            CapturingChatClient reply = new(_ => Task.CompletedTask, "a2");
            CompactionStages stages = new(
                new ToolResultCapProvider(CompactionTriggers.Always, keepTurns: 0, maxResultChars: 3),
                new ScriptedChatClient("never"),
                client => new SummarizationCompactionStrategy(client, CompactionTriggers.Never));
            ConversationSession session = CreateSession(reply, store, conversationId: "c1", compaction: stages);

            _ = await session.RunTurnAsync("q2", TestContext.Current.CancellationToken);
            await session.FlushTranscriptAsync();

            List<string> request = Assert.Single(reply.Requests);
            Assert.Contains("tool:res…", request);
            Assert.DoesNotContain("tool:result-A", request);

            Assert.DoesNotContain(await store.ReadForSessionAsync("c1", TestContext.Current.CancellationToken), row => row.CoversUpTo is not null);
            IReadOnlyList<ConversationMessage> stored = await store.ReadAllAsync("c1", TestContext.Current.CancellationToken);
            Assert.Equal(8, stored.Count);
            Assert.Contains(stored, row => row.Content.Contents.OfType<FunctionResultContent>().Any(result => result.Result?.ToString() == "result-A"));
        }

        [Fact]
        public async Task ATurn_WhoseViewCrossesThreeQuartersOfTheWindow_FiresTheRealPipeline()
        {
            // A 400-token window fires at 300 and stops at 200 (D7). One short turn stays well under;
            // twelve turns of ~80 characters each land well over.
            const int Window = 400;
            InMemoryConversationStore store = new();
            _ = await store.CreateAsync("c1", TestContext.Current.CancellationToken);
            await SeedPlainTurnAsync(store, turnIndex: 0, "q0", "a0");

            ConversationSession quiet = CreateSession(new ScriptedChatClient("a1"), store, conversationId: "c1", contextWindow: Window);
            _ = await quiet.RunTurnAsync("q1", TestContext.Current.CancellationToken);
            await quiet.FlushTranscriptAsync();

            Assert.DoesNotContain(await store.ReadForSessionAsync("c1", TestContext.Current.CancellationToken), row => row.CoversUpTo is not null);

            string padding = new('w', 80);
            for (int turn = 2; turn < 14; turn++)
            {
                await SeedPlainTurnAsync(store, turn, $"q{turn} {padding}", $"a{turn} {padding}");
            }

            ConversationSession loud = CreateSession(new ScriptedChatClient("the gist of it"), store, conversationId: "c1", contextWindow: Window);
            _ = await loud.RunTurnAsync("q14", TestContext.Current.CancellationToken);
            await loud.FlushTranscriptAsync();

            ConversationMessage summary = Assert.Single(await store.ReadForSessionAsync("c1", TestContext.Current.CancellationToken), row => row.CoversUpTo is not null);
            Assert.Contains("the gist of it", summary.Content.Text, StringComparison.Ordinal);
            Assert.DoesNotContain(await store.ReadAllAsync("c1", TestContext.Current.CancellationToken), row => row.CoversUpTo is not null);
        }

        [Fact]
        public async Task ANewSession_OfAConversationWithASummary_OpensOnTheSummarisedView()
        {
            InMemoryConversationStore store = new();
            _ = await store.CreateAsync("c1", TestContext.Current.CancellationToken);
            await SeedPlainTurnAsync(store, turnIndex: 0, "q0", "a0");
            await SeedPlainTurnAsync(store, turnIndex: 1, "q1", "a1");
            await SeedPlainTurnAsync(store, turnIndex: 2, "q2", "a2");

            ConversationSession first = CreateSession(new ScriptedChatClient("a3"), store, conversationId: "c1", compaction: Summary(new ScriptedChatClient("the gist of it"), minimumPreservedGroups: 0));
            _ = await first.RunTurnAsync("q3", TestContext.Current.CancellationToken);
            await first.FlushTranscriptAsync();

            CapturingChatClient reply = new(_ => Task.CompletedTask, "a4");
            ConversationSession resumed = CreateSession(reply, store, conversationId: "c1");
            _ = await resumed.RunTurnAsync("q4", TestContext.Current.CancellationToken);

            Assert.Equal(["assistant:[Summary]\nthe gist of it", "user:q2", "assistant:a2", "user:q3", "assistant:a3", "user:q4"], Assert.Single(reply.Requests));
            Assert.NotNull(resumed.Compiled.History.Summary(resumed.AgentSession!));
        }
    }
#pragma warning restore MAAI001
}
