using AgentCore.Application.Evaluation;
using AgentCore.Application.Tests.Evaluation.Fakes;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Transcript;
using AgentCore.TestSupport;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Application.Runtime.Agents;
using AgentCore.Application.Runtime.Turn;
using AgentCore.Application.Runtime.Turn.Lifecycle;

namespace AgentCore.Application.Tests.Runtime
{
    /// <summary>
    /// A harness over the real types: one <see cref="ChatClientAgent"/> with a tool, the real
    /// fallback and moderation layers, and <see cref="ConversationTurnAgent"/> outermost, all on one real provider.
    /// </summary>
    internal sealed class ConversationTurnAgentHarness
    {
        internal const string ConversationId = "c";

        private ConversationTurnAgentHarness(
            AgentCoreChatHistoryProvider history, RecordingConversationStore store, AIAgent agent, AgentSession session)
        {
            History = history;
            Store = store;
            Agent = agent;
            Session = session;
        }

        public AgentCoreChatHistoryProvider History { get; }

        public RecordingConversationStore Store { get; }

        public AIAgent Agent { get; }

        public AgentSession Session { get; }

        /// <summary>Gets the order the completer and the store saw: <c>staged</c>, <c>complete</c>, <c>append</c>.</summary>
        public List<string> Log { get; } = [];

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        public static async Task<ConversationTurnAgentHarness> CreateAsync(
            IChatClient model, ScriptedModerationEvaluator? moderation = null)
        {
            RecordingConversationStore store = new();
            _ = await store.CreateAsync(ConversationId, Ct);
            AgentCoreChatHistoryProvider history = new(store);

            AIAgent layers = new FallbackAgent(ToolAgent(model, history, "only"), "fallback");
            if (moderation is not null)
            {
                layers = new ModerationAgent(layers, new PromptModerator(moderation), "refused", TimeSpan.FromSeconds(2));
            }

            ConversationTurnAgent top = new(layers, history);
            AgentSession session = await top.CreateSessionAsync(Ct);
            _ = history.BeginConversation(session, ConversationId, []);

            ConversationTurnAgentHarness harness = new(history, store, top, session);
            store.Log = harness.Log;
            return harness;
        }

        /// <summary>Builds one tool-carrying chat agent on the provider.</summary>
        public static ChatClientAgent ToolAgent(IChatClient model, ChatHistoryProvider history, string name)
        {
            return new ChatClientAgent(
                new FunctionInvokingChatClient(model),
                new ChatClientAgentOptions
                {
                    Name = name,
                    ChatOptions = new ChatOptions { Tools = [AIFunctionFactory.Create(() => "42", "lookup")] },
                    ChatHistoryProvider = history,
                });
        }

        /// <summary>Opens one turn the way the runner files it.</summary>
        public HarnessTurn Begin(string text, int turnIndex, ConversationTurnOrigin? origin = null, CancellationTokenSource? host = null)
        {
            History.BeginTurn(Session, turnIndex);
            return HarnessTurn.Open(History, Session, text, turnIndex, carriesHistory: true, Log, origin, host);
        }

        /// <summary>Streams one opened turn to its end.</summary>
        /// <param name="turn">The opened turn.</param>
        /// <param name="onUpdate">Sees each update as the reader gets it, or <see langword="null"/>.</param>
        public async Task<List<AgentResponseUpdate>> DrainAsync(HarnessTurn turn, Action<AgentResponseUpdate>? onUpdate = null)
        {
            List<AgentResponseUpdate> updates = [];
            await foreach (AgentResponseUpdate update in Agent.RunStreamingAsync(
                [turn.Invocation.User!], Session, turn.Invocation.RunOptions(), turn.Slot.Token))
            {
                updates.Add(update);
                onUpdate?.Invoke(update);
            }

            await History.DrainAsync(Session);
            return updates;
        }

        /// <summary>Runs one turn to its end, streaming.</summary>
        public async Task<HarnessTurn> RunAsync(string text, int turnIndex, ConversationTurnOrigin? origin = null)
        {
            HarnessTurn turn = Begin(text, turnIndex, origin);
            turn.Updates = await DrainAsync(turn);
            return turn;
        }
    }
}
