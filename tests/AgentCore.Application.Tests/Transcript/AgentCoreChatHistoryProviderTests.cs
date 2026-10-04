using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Transcript;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;
using static AgentCore.Application.Tests.Transcript.AgentCoreChatHistoryProviderTestSupport;

namespace AgentCore.Application.Tests.Transcript
{
    /// <summary>
    /// Pins what the provider adds to <see cref="ConversationTranscript"/>: the lock, the write chain, and the
    /// rule that a store failure never reaches the turn.
    /// </summary>
    public sealed class AgentCoreChatHistoryProviderTests
    {
        [Fact]
        public async Task ProvideChatHistory_AfterAppend_ReturnsMessagesInOrder()
        {
            (AgentCoreChatHistoryProvider? provider, RecordingConversationStore _, StubSession? session) = await NewConversation();
            AppendTurn(provider, session, turnIndex: 0, "hello", "hi there");
            AppendTurn(provider, session, turnIndex: 1, "order 41?", "it ships Friday");

            IReadOnlyList<ChatMessage> history = await ProvideAsync(provider, session);

            Assert.Equal(
                ["hello", "hi there", "order 41?", "it ships Friday"],
                history.Select(message => message.Text));
        }

        [Fact]
        public async Task AppendTurn_MultipleTurns_OrdinalsAreDenseAndUnique()
        {
            (AgentCoreChatHistoryProvider? provider, RecordingConversationStore? store, StubSession? session) = await NewConversation();
            AppendTurn(provider, session, turnIndex: 0, "hello", "hi there");

            AppendTurn(provider, session, turnIndex: 1, "order 41?", "it ships Friday");

            await provider.DrainAsync(session);
            Assert.Equal([0, 1, 2, 3], store.Rows.Select(row => row.Ordinal));
            Assert.Equal([0, 0, 1, 1], store.Rows.Select(row => row.TurnIndex));
            Assert.All(store.Rows, row => Assert.Equal(ConversationId, row.ConversationId));
        }

        [Fact]
        public async Task AppendTurn_RefusedTurn_IsStored()
        {
            (AgentCoreChatHistoryProvider? provider, RecordingConversationStore? store, StubSession? session) = await NewConversation();

            AppendTurn(provider, session, turnIndex: 0, "something flagged", "I can't help with that.");

            await provider.DrainAsync(session);
            Assert.Equal(
                ["something flagged", "I can't help with that."],
                store.Rows.Select(row => row.Content.Text));
        }

        [Fact]
        public async Task AppendTurn_FailedTurn_IsStored()
        {
            (AgentCoreChatHistoryProvider? provider, RecordingConversationStore? store, StubSession? session) = await NewConversation();

            AppendTurn(provider, session, turnIndex: 0, "check my order", "Sorry, I had trouble with that.");

            await provider.DrainAsync(session);
            Assert.Equal(
                ["check my order", "Sorry, I had trouble with that."],
                store.Rows.Select(row => row.Content.Text));
        }

        // The hook stages and writes nothing durable. A later run of the same turn reads what the
        // finished run was told and answered after the conversation.
        [Fact]
        public async Task StoreChatHistory_FinishedRun_StagesTheResponseAndStoresNothing()
        {
            (AgentCoreChatHistoryProvider? provider, RecordingConversationStore? store, StubSession? session) = await NewConversation();
            provider.BeginTurn(session, turnIndex: 0);

#pragma warning disable MAAI001 // The context constructors are the framework's own experimental surface.
            await provider.InvokedAsync(
                new ChatHistoryProvider.InvokedContext(
                    StubAgent.Instance,
                    session,
                    [new ChatMessage(ChatRole.User, "<system-reminder>ask for the id</system-reminder>\norder 41?")],
                    [new ChatMessage(ChatRole.Assistant, "it ships Friday")]),
                TestContext.Current.CancellationToken);
#pragma warning restore MAAI001

            await provider.DrainAsync(session);
            Assert.Empty(store.Rows);
            Assert.Equal(
                ["<system-reminder>ask for the id</system-reminder>\norder 41?", "it ships Friday"],
                (await ProvideAsync(provider, session)).Select(message => message.Text));
            Assert.Equal(["it ships Friday"], provider.Staged(session).Select(message => message.Text));
        }

        [Fact]
        public async Task AppendTurn_ConcurrentAppends_LosesNoMessage()
        {
            (AgentCoreChatHistoryProvider? provider, RecordingConversationStore? store, StubSession? session) = await NewConversation();
            provider.BeginTurn(session, turnIndex: 0);
            TaskCompletionSource start = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task[] turns = [.. Enumerable.Range(0, 20)
                .Select(index => Task.Run(
                    async () =>
                    {
                        await start.Task;
                        _ = provider.CommitTurn(
                            session,
                            new TurnCommit(new ChatMessage(ChatRole.User, $"said {index}"))
                            {
                                Seen = new AgentResponse(new ChatMessage(ChatRole.Assistant, $"replied {index}")),
                            });
                    },
                    TestContext.Current.CancellationToken))];

            start.SetResult();
            await Task.WhenAll(turns);

            await provider.DrainAsync(session);
            Assert.Equal(40, store.Rows.Count);
            Assert.Equal(Enumerable.Range(0, 40), store.Rows.Select(row => row.Ordinal).Order());
        }

        [Fact]
        public async Task ProvideChatHistory_TwoConcurrentSessions_DoNotMix()
        {
            AgentCoreChatHistoryProvider provider = new(new RecordingConversationStore());
            StubSession first = new();
            StubSession second = new();
            _ = provider.BeginConversation(first, "conversation-a", []);
            _ = provider.BeginConversation(second, "conversation-b", []);
            AppendTurn(provider, first, turnIndex: 0, "a said", "a heard");

            AppendTurn(provider, second, turnIndex: 0, "b said", "b heard");

            Assert.Equal(["a said", "a heard"], (await ProvideAsync(provider, first)).Select(message => message.Text));
            Assert.Equal(["b said", "b heard"], (await ProvideAsync(provider, second)).Select(message => message.Text));
        }

        // StateKeys is ["agentcore.history"].
        [Fact]
        public void StateKeys_IsTheConversationKey()
        {
            AgentCoreChatHistoryProvider provider = new();

            Assert.Equal([StateKey], provider.StateKeys);
        }

        // BeginConversation files the conversation id under the provider's key, and nothing else enters the bag.
        [Fact]
        public async Task AppendTurn_LeavesOnlyTheConversationKeyInTheStateBag()
        {
            (AgentCoreChatHistoryProvider? provider, RecordingConversationStore _, StubSession? session) = await NewConversation();

            AppendTurn(provider, session, turnIndex: 0, "order 41?", "it ships Friday");

            Assert.Equal(1, session.StateBag.Count);
            Assert.True(session.StateBag.TryGetValue(StateKey, out string? conversationId));
            Assert.Equal(ConversationId, conversationId);
            Assert.Equal(["order 41?", "it ships Friday"], provider.Read(session).Select(message => message.Text));
        }

        /// <summary>
        /// The provider is one object shared by every conversation, so two sessions must still reach two
        /// transcripts. State held against the provider rather than the session would merge them.
        /// </summary>
        [Fact]
        public void AppendTurn_TwoSessions_EachHoldsItsOwnTranscript()
        {
            AgentCoreChatHistoryProvider provider = new(new RecordingConversationStore());
            StubSession first = new();
            StubSession second = new();
            _ = provider.BeginConversation(first, "conversation-a", []);
            _ = provider.BeginConversation(second, "conversation-b", []);

            AppendTurn(provider, first, turnIndex: 0, "a said", "a heard");
            AppendTurn(provider, second, turnIndex: 0, "b said", "b heard");

            Assert.Equal(["a said", "a heard"], provider.Read(first).Select(message => message.Text));
            Assert.Equal(["b said", "b heard"], provider.Read(second).Select(message => message.Text));
        }

        [Fact]
        public async Task AppendTurn_BackingStoreThrows_DoesNotFailTheTurn()
        {
            AgentCoreChatHistoryProvider provider = new(new ThrowingConversationStore());
            StubSession session = new();
            _ = provider.BeginConversation(session, ConversationId, []);

            AppendTurn(provider, session, turnIndex: 0, "hello", "hi there");

            await provider.DrainAsync(session);
            Assert.Equal(["hello", "hi there"], (await ProvideAsync(provider, session)).Select(message => message.Text));
        }

        [Fact]
        public async Task BeginConversation_BackingStoreThrows_TellsTheReporterWhichTurnWasLost()
        {
            AgentCoreChatHistoryProvider provider = new(new ThrowingConversationStore());
            StubSession session = new();
            DroppedTurns dropped = new();
            _ = provider.BeginConversation(session, ConversationId, [], dropped);

            AppendTurn(provider, session, turnIndex: 3, "hello", "hi there");

            await provider.DrainAsync(session);
            Assert.Equal([3], dropped.Turns);
        }

        private sealed class DroppedTurns : ITranscriptLossCounter
        {
            public List<int> Turns { get; } = [];

            public void Dropped(int turnIndex, IReadOnlyList<string> appended, Exception exception)
            {
                Turns.Add(turnIndex);
            }

            public void Withdrew(IReadOnlyList<string> messageIds)
            {
            }

            public ValueTask<TranscriptLossVerdict?> JudgeAsync(AgentSession session, CancellationToken cancellationToken)
            {
                return ValueTask.FromResult<TranscriptLossVerdict?>(null);
            }

            public void Realigned(TranscriptLossVerdict verdict)
            {
            }
        }
    }
}
