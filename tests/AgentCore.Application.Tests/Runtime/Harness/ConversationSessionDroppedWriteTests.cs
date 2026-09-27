using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Conversation;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Runtime.Compaction;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Domain;
using AgentCore.TestSupport;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Xunit;
using static AgentCore.Application.Tests.Transcript.ConversationSessionCompactionTestSupport;

namespace AgentCore.Application.Tests.Runtime.Harness
{
    /// <summary>
    /// A store 1 append that fails, or that the store saved and then reported failed, while no other session writes the
    /// conversation: the next turn takes the store's words back and keeps the session's own harness state, which its
    /// commit then writes again. Only a store that another session wrote in the meantime replaces that state (the P1
    /// case in <see cref="ConversationSessionProviderStateTests"/>).
    /// </summary>
    public sealed class ConversationSessionDroppedWriteTests
    {
        private const string TodosYaml =
            """
          apiVersion: agentcore/v1
          agents:
            items:
              - { id: only, instructions: "track todos", todos: true }
          entries:
            main:
              agent: only
          """;

        private const string LingeringChildYaml =
            """
          apiVersion: agentcore/v1
          agents:
            items:
              - { id: parent, instructions: "delegate work", model: { ref: parent }, background: [blocker] }
              - { id: blocker, instructions: "work forever", model: { ref: blocker } }
          entries:
            main:
              agent: parent
          """;

        private static CancellationToken Token => TestContext.Current.CancellationToken;

        [Fact]
        public async Task OneDroppedAppend_WithNoOtherWriter_KeepsTheTodoThatTurnAdded_AndWritesItAtTheNextCommit()
        {
            FlakyAppendConversationStore store = new();
            CueToolChatClient model = new("track", () => new(StringComparer.Ordinal)
            {
                ["todos"] = new List<object> { new Dictionary<string, object?>(StringComparer.Ordinal) { ["title"] = "buy milk" } },
            });
            await using ConversationSession session = Build(TodosYaml, store, ("only", model)).Create("conversation-1");

            _ = await session.RunTurnAsync("hello", Token);
            store.Down = true;
            _ = await session.RunTurnAsync("please track this", Token);
            await session.FlushTranscriptAsync();
            store.Down = false;

            _ = await session.RunTurnAsync("what's on my list", Token);
            await session.FlushTranscriptAsync();

            Assert.Contains(model.Requests[^1], message => message.Text.Contains("buy milk", StringComparison.Ordinal));
            ConversationRecord? record = await store.GetAsync("conversation-1", Token);
            Assert.Contains("buy milk", record!.State!.Providers[new TodoProvider().StateKeys[0]].GetRawText(), StringComparison.Ordinal);
        }

        [Fact]
        public async Task OneDroppedAppend_WithNoOtherWriter_KeepsTheSession_AndItsBackgroundChildRunning()
        {
            FlakyAppendConversationStore store = new();
            using HangUntilCancelledChatClient child = new();
            ToolCallingChatClient parent = new("started", new(StringComparer.Ordinal)
            {
                ["agentName"] = "blocker",
                ["input"] = "work",
                ["description"] = "d",
            });
            await using ConversationSession session = Build(LingeringChildYaml, store, ("parent", parent), ("blocker", child)).Create("conversation-1");

            _ = await session.RunTurnAsync("go", Token);
            await child.Started.WaitAsync(TimeSpan.FromSeconds(10), Token);
            store.Down = true;
            _ = await session.RunTurnAsync("again", Token);
            await session.FlushTranscriptAsync();
            store.Down = false;
            AgentSession? before = session.Ledger.Session();

            TurnResult next = await session.RunTurnAsync("status?", Token);

            Assert.Equal(2, next.TurnIndex);
            Assert.Same(before, session.Ledger.Session());
            Assert.False(child.Cancelled.IsCompleted);
        }

        [Fact]
        public async Task OneAppendTheStoreSavedButReportedFailed_WithNoOtherWriter_KeepsTheSession_AndItsBackgroundChildRunning()
        {
            FlakyAppendConversationStore store = new();
            using HangUntilCancelledChatClient child = new();
            ToolCallingChatClient parent = new("started", new(StringComparer.Ordinal)
            {
                ["agentName"] = "blocker",
                ["input"] = "work",
                ["description"] = "d",
            });
            await using ConversationSession session = Build(LingeringChildYaml, store, ("parent", parent), ("blocker", child)).Create("conversation-1");

            _ = await session.RunTurnAsync("go", Token);
            await child.Started.WaitAsync(TimeSpan.FromSeconds(10), Token);
            store.SavesThenFails = true;
            _ = await session.RunTurnAsync("again", Token);
            await session.FlushTranscriptAsync();
            store.SavesThenFails = false;
            AgentSession? before = session.Ledger.Session();

            TurnResult next = await session.RunTurnAsync("status?", Token);
            await session.FlushTranscriptAsync();

            Assert.Equal(2, next.TurnIndex);
            Assert.Same(before, session.Ledger.Session());
            Assert.False(child.Cancelled.IsCompleted);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ALostAppend_ThenAnEditUnderTheSummaryInATurnWhoseCatchUpCouldNotRead_KeepsTheSession(bool savedThenFailed)
        {
            FlakyAppendConversationStore store = new();
            _ = await store.CreateAsync("c1", Token);
            await SeedPlainTurnAsync(store, turnIndex: 0, "q0", "a0");
            await SeedPlainTurnAsync(store, turnIndex: 1, "q1", "a1");
            await SeedPlainTurnAsync(store, turnIndex: 2, "q2", "a2");
            await using ConversationSession session = Build(
                TodosYaml, store, Summary(new ScriptedChatClient("the gist of it"), minimumPreservedGroups: 0), new ScriptedChatClient("ok")).Create("c1");

            _ = await session.RunTurnAsync("q3", Token);
            store.SavesThenFails = savedThenFailed;
            store.Down = !savedThenFailed;
            _ = await session.RunTurnAsync("q4", Token);
            await session.FlushTranscriptAsync();
            store.SavesThenFails = false;
            store.Down = false;

            // "m-0-a" is under the summary, so the edit cuts the store and reads the session's words back from it.
            store.RecordReadsToFail = 1;
            _ = await session.RunTurnAtOriginAsync("q0, rewritten", new ConversationTurnOrigin("caller-2", "m-0-a") { NamesParent = true }, Token);
            await session.FlushTranscriptAsync();
            Assert.Equal(0, store.RecordReadsToFail);
            AgentSession? before = session.Ledger.Session();

            _ = await session.RunTurnAsync("q5", Token);

            Assert.Same(before, session.Ledger.Session());
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ALostAppend_ThenALateCutThatRemovesItsReply_WithNoOtherWriter_KeepsTheSession_AndItsBackgroundChildRunning(bool savedThenFailed)
        {
            FlakyAppendConversationStore store = new();
            using HangUntilCancelledChatClient child = new();
            ToolCallingChatClient parent = new("started", new(StringComparer.Ordinal)
            {
                ["agentName"] = "blocker",
                ["input"] = "work",
                ["description"] = "d",
            });
            await using ConversationSession session = Build(LingeringChildYaml, store, ("parent", parent), ("blocker", child)).Create("conversation-1");

            _ = await session.RunTurnAsync("go", Token);
            await child.Started.WaitAsync(TimeSpan.FromSeconds(10), Token);
            store.SavesThenFails = savedThenFailed;
            store.Down = !savedThenFailed;
            _ = await session.RunTurnAsync("again", Token);
            await session.FlushTranscriptAsync();
            store.SavesThenFails = false;
            store.Down = false;

            // A barge-in before any word of turn 1's reply played leaves its reply row empty, so the session deletes it.
            Assert.True(session.Cut(1, new TurnCut(string.Empty, null)));
            await session.FlushTranscriptAsync();
            AgentSession? before = session.Ledger.Session();

            TurnResult next = await session.RunTurnAsync("status?", Token);
            await session.FlushTranscriptAsync();

            Assert.Equal(2, next.TurnIndex);
            Assert.Same(before, session.Ledger.Session());
            Assert.False(child.Cancelled.IsCompleted);
        }

        [Theory]
        [InlineData(false, 1)]
        [InlineData(true, 1)]
        [InlineData(false, 2)]
        [InlineData(true, 2)]
        public async Task ALostAppend_ThenAnInSessionEditWithdrawingIt_InATurnWhoseCatchUpCouldNotRead_KeepsTheSession(bool savedThenFailed, int readsThatFail)
        {
            FlakyAppendConversationStore store = new();
            _ = await store.CreateAsync("c1", Token);
            await SeedPlainTurnAsync(store, turnIndex: 0, "q0", "a0");
            await SeedPlainTurnAsync(store, turnIndex: 1, "q1", "a1");
            await using ConversationSession session = Build(TodosYaml, store, ("only", new ScriptedChatClient("ok"))).Create("c1");

            _ = await session.RunTurnAsync("q2", Token);
            store.SavesThenFails = savedThenFailed;
            store.Down = !savedThenFailed;
            _ = await session.RunTurnAsync("q3", Token);
            await session.FlushTranscriptAsync();
            store.SavesThenFails = false;
            store.Down = false;

            // "m-1-a" is under no summary, so the session truncates its own words from turn 2 on. The second failed
            // read is the one that would judge the lost append before the truncate deletes every row of it.
            store.RecordReadsToFail = readsThatFail;
            _ = await session.RunTurnAtOriginAsync("q2, rewritten", new ConversationTurnOrigin("caller-2", "m-1-a") { NamesParent = true }, Token);
            await session.FlushTranscriptAsync();
            Assert.Equal(0, store.RecordReadsToFail);
            AgentSession? before = session.Ledger.Session();

            _ = await session.RunTurnAsync("q5", Token);

            Assert.Same(before, session.Ledger.Session());
        }

        [Fact]
        public async Task ADroppedAppend_ThenAnotherSessionsTurnInItsPlace_ThenAnInSessionEditWithdrawingBoth_StillRebuildsTheSession()
        {
            FlakyAppendConversationStore store = new();
            _ = await store.CreateAsync("c1", Token);
            await SeedPlainTurnAsync(store, turnIndex: 0, "q0", "a0");
            await SeedPlainTurnAsync(store, turnIndex: 1, "q1", "a1");
            await using ConversationSession session = Build(TodosYaml, store, ("only", new ScriptedChatClient("ok"))).Create("c1");
            await using ConversationSession other = Build(TodosYaml, store, ("only", new ScriptedChatClient("ok"))).Create("c1");

            _ = await session.RunTurnAsync("q2", Token);
            store.Down = true;
            _ = await session.RunTurnAsync("q3", Token);
            await session.FlushTranscriptAsync();
            store.Down = false;

            // The other session saves its own turn 3, with as many rows as the lost one, where the lost one would stand.
            _ = await other.RunTurnAsync("q3 from elsewhere", Token);
            await other.FlushTranscriptAsync();

            store.RecordReadsToFail = 1;
            _ = await session.RunTurnAtOriginAsync("q2, rewritten", new ConversationTurnOrigin("caller-2", "m-1-a") { NamesParent = true }, Token);
            await session.FlushTranscriptAsync();
            Assert.Equal(0, store.RecordReadsToFail);
            AgentSession? before = session.Ledger.Session();

            _ = await session.RunTurnAsync("q5", Token);

            Assert.NotSame(before, session.Ledger.Session());
        }

        [Fact]
        public async Task OneAppendTheStoreSavedButReportedFailed_ThenASummaryOverItWhileTheStoreCannotBeRead_KeepsTheSession()
        {
            FlakyAppendConversationStore store = new();
            _ = await store.CreateAsync("c1", Token);
            bool compacting = false;
#pragma warning disable MAAI001 // Compaction is evaluation-only in Microsoft.Agents.AI 1.21.0.
            CompactionStages compaction = SummaryOnly(
                new ScriptedChatClient("the gist of it"), client => new SummarizationCompactionStrategy(client, _ => compacting, minimumPreservedGroups: 0));
#pragma warning restore MAAI001
            await using ConversationSession session = Build(TodosYaml, store, compaction, new ScriptedChatClient("ok")).Create("c1");

            _ = await session.RunTurnAsync("q0", Token);
            store.SavesThenFails = true;
            _ = await session.RunTurnAsync("q1", Token);
            await session.FlushTranscriptAsync();
            store.SavesThenFails = false;

            // The second of these summaries covers turn 1, so the rows the store saved are no longer read for the session.
            compacting = true;
            store.RecordReadsToFail = 2;
            _ = await session.RunTurnAsync("q2", Token);
            _ = await session.RunTurnAsync("q3", Token);
            await session.FlushTranscriptAsync();
            Assert.Equal(0, store.RecordReadsToFail);
            Assert.DoesNotContain(await store.ReadForSessionAsync("c1", Token), row => row.Content.Text == "q1");
            AgentSession? before = session.Ledger.Session();

            _ = await session.RunTurnAsync("q4", Token);

            Assert.Same(before, session.Ledger.Session());
        }

        private static ConversationSessionFactory Build(string yaml, IConversationStore store, CompactionStages compaction, IChatClient model)
        {
            CompiledAgent compiled = ConfigurationCompiler.CompileAll(
                ConfigurationLoader.LoadYaml(yaml),
                new AgentCompilationContext(new RoutingChatClientFactory(model)) { ConversationStore = store, Compaction = compaction })["main"];

            return new ConversationSessionFactory(compiled, new GuardEvaluator(compiled.Configuration.Guards));
        }

        private static ConversationSessionFactory Build(string yaml, IConversationStore store, params (string Ref, IChatClient Client)[] routes)
        {
            RoutingChatClientFactory chatClients = new(routes[0].Client);
            foreach ((string name, IChatClient client) in routes)
            {
                _ = chatClients.Route(name, client);
            }

            CompiledAgent compiled = ConfigurationCompiler.CompileAll(
                ConfigurationLoader.LoadYaml(yaml), new AgentCompilationContext(chatClients) { ConversationStore = store })["main"];

            return new ConversationSessionFactory(compiled, new GuardEvaluator(compiled.Configuration.Guards));
        }
    }
}
