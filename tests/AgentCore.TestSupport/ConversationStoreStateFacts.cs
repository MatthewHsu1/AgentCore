using AgentCore.Application.Conversation;
using AgentCore.Application.Ports;
using AgentCore.Application.Transcript;
using Microsoft.Extensions.AI;

namespace AgentCore.TestSupport
{
    /// <summary>
    /// What every store must do with a state no words carry (<see cref="IConversationStore.SaveStateAsync"/>): keep it
    /// when it is level with the stored state or newer, and drop it when it is behind. Each store's test class runs
    /// these against its own instance. A fact that fails throws <see cref="InvalidOperationException"/>.
    /// </summary>
    public static class ConversationStoreStateFacts
    {
        [AssertionMethod]
        public static async Task AStateLevelWithTheStoredOneReplacesIt(IConversationStore store, CancellationToken cancellationToken)
        {
            await SeedAsync(store, cancellationToken);

            await store.SaveStateAsync("c1", new ConversationSessionState { NextTurnIndex = 1, Stage = "done", IsComplete = true }, cancellationToken);

            Expect("1/done/True", await StoredAsync(store, cancellationToken), "the state after a level write");
        }

        [AssertionMethod]
        public static async Task AStateBehindTheStoredOneIsDropped(IConversationStore store, CancellationToken cancellationToken)
        {
            await SeedAsync(store, cancellationToken);

            await store.SaveStateAsync("c1", new ConversationSessionState { NextTurnIndex = 0, Stage = "done", IsComplete = true }, cancellationToken);

            Expect("1/working/False", await StoredAsync(store, cancellationToken), "the state after a write behind it");
        }

        [AssertionMethod]
        public static async Task AConversationWithNoRowIsLeftAlone(IConversationStore store, CancellationToken cancellationToken)
        {
            await store.SaveStateAsync("missing", new ConversationSessionState { NextTurnIndex = 1, IsComplete = true }, cancellationToken);

            if (await store.GetAsync("missing", cancellationToken) is not null)
            {
                throw new InvalidOperationException("a state write made a conversation that had no row.");
            }
        }

        private static async Task SeedAsync(IConversationStore store, CancellationToken cancellationToken)
        {
            _ = await store.CreateAsync("c1", cancellationToken);
            _ = await store.AppendAsync(
                "c1",
                [new ConversationMessageDraft(0, new ChatMessage(ChatRole.User, "hi"), "m-0-u")],
                new ConversationSessionState { NextTurnIndex = 1, Stage = "working" },
                cancellationToken);
        }

        private static async Task<string> StoredAsync(IConversationStore store, CancellationToken cancellationToken)
        {
            ConversationSessionState? state = (await store.GetAsync("c1", cancellationToken))?.State;
            return state is null ? "none" : $"{state.NextTurnIndex}/{state.Stage}/{state.IsComplete}";
        }

        private static void Expect(string expected, string actual, string what)
        {
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"{what}: expected {expected}, saw {actual}.");
            }
        }
    }
}
