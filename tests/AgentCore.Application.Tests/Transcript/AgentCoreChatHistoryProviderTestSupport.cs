using System.Text.Json;
using AgentCore.Application.Conversation;
using AgentCore.Application.Conversation.Memory;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Transcript;
using AgentCore.TestSupport;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Transcript
{
    /// <summary>What both halves of the provider's test suite share: the conversation id, the session state
    /// key, and the plumbing to open a conversation and read its history back.</summary>
    internal static class AgentCoreChatHistoryProviderTestSupport
    {
        internal const string ConversationId = "conversation-1";

        /// <summary>The key that binds a session to its conversation.</summary>
        internal const string StateKey = "agentcore.history";

        /// <summary>
        /// Opens one conversation on a fresh session, the way <c>ConversationSession</c> does at conversation start: the row is
        /// made before any turn can append against it, exactly as <c>ConversationSession.OpenSessionAsync</c>
        /// makes it before it ever reaches this provider.
        /// </summary>
        internal static async Task<(AgentCoreChatHistoryProvider Provider, RecordingConversationStore Store, StubSession Session)> NewConversation()
        {
            RecordingConversationStore store = new();
            _ = await store.CreateAsync(ConversationId, TestContext.Current.CancellationToken);
            AgentCoreChatHistoryProvider provider = new(store);
            StubSession session = new();
            _ = provider.BeginConversation(session, ConversationId, []);
            return (provider, store, session);
        }

        /// <summary>Writes one turn the way <c>ConversationTurnAgent</c> does: name the turn, then commit it.</summary>
        internal static void AppendTurn(
            AgentCoreChatHistoryProvider provider,
            AgentSession session,
            int turnIndex,
            string said,
            string replied)
        {
            provider.BeginTurn(session, turnIndex);
            _ = provider.CommitTurn(
                session,
                new TurnCommit(new ChatMessage(ChatRole.User, said)) { Seen = new AgentResponse(new ChatMessage(ChatRole.Assistant, replied)) });
        }


        internal static async Task<IReadOnlyList<ChatMessage>> ProvideAsync(
            AgentCoreChatHistoryProvider provider, AgentSession session)
        {
#pragma warning disable MAAI001 // The context constructors are the framework's own experimental surface.
            ChatHistoryProvider.InvokingContext context = new(StubAgent.Instance, session, []);
#pragma warning restore MAAI001
            IEnumerable<ChatMessage> messages = await provider.InvokingAsync(context, TestContext.Current.CancellationToken);
            return [.. messages];
        }
    }

    /// <summary>
    /// Holds one append open, so a barge-in can arrive while a turn is still writing. It keeps the
    /// real store's ordering rule: a rewrite of a row that is not there yet changes nothing.
    /// </summary>
    internal sealed class BlockingConversationStore() : DelegatingConversationStore(new InMemoryConversationStore())
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _block;

        public Task Entered => _entered.Task;

        public void BlockNextAppend()
        {
            _block = true;
        }

        public void Release()
        {
            _ = _release.TrySetResult();
        }

        public override async ValueTask<IReadOnlyList<ConversationMessage>> AppendAsync(
            string conversationId,
            IReadOnlyList<ConversationMessageDraft> messages,
            ConversationSessionState? state = null,
            CancellationToken cancellationToken = default)
        {
            if (_block)
            {
                _block = false;
                _ = _entered.TrySetResult();
                await _release.Task.WaitAsync(cancellationToken);
            }

            return await Inner.AppendAsync(conversationId, messages, state, cancellationToken);
        }

        public override ValueTask RewriteAsync(
            string conversationId, string messageId, ChatMessage content, CancellationToken cancellationToken = default)
        {
            return Inner.RewriteAsync(conversationId, messageId, content, cancellationToken);
        }
    }

    internal sealed class StubSession : AgentSession;

    /// <summary>Stands in for the agent the framework names on a context. Nothing here runs it.</summary>
    internal sealed class StubAgent : AIAgent
    {
        public static StubAgent Instance { get; } = new();

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(
            CancellationToken cancellationToken = default)
        {
            return new(new StubSession());
        }

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement serializedState,
            JsonSerializerOptions? jsonSerializerOptions = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
