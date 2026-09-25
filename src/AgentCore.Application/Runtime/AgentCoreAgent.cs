using AgentCore.Application.Conversation;
using AgentCore.Application.Diagnostics;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime.Turn;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using AgentCore.Domain;

namespace AgentCore.Application.Runtime
{
    /// <summary>
    /// The whole turn loop of this library, behind the framework's own agent abstraction.
    /// </summary>
    public sealed class AgentCoreAgent : AIAgent
    {
        private readonly IConversationSessionFactory _sessions;

        private readonly string? _description;

        /// <summary>Creates the shim over one compiled entry's turn loop.</summary>
        /// <param name="sessions">The factory that starts one <see cref="ConversationSession"/> for each conversation.</param>
        /// <param name="entryName">The entry this agent serves. Reported as <see cref="Name"/>.</param>
        /// <param name="description">The description the agent reports, or <see langword="null"/>.</param>
        /// <exception cref="ArgumentNullException"><paramref name="sessions"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="entryName"/> is null or empty.</exception>
        public AgentCoreAgent(IConversationSessionFactory sessions, string entryName, string? description = null)
        {
            ArgumentNullException.ThrowIfNull(sessions);
            ArgumentException.ThrowIfNullOrEmpty(entryName);

            _sessions = sessions;
            EntryName = entryName;
            _description = description;
        }

        /// <summary>Gets the entry this agent serves.</summary>
        public string EntryName { get; }

        /// <inheritdoc />
        public override string? Name => EntryName;

        /// <inheritdoc />
        public override string? Description => _description;

        /// <summary>Starts one conversation under the id the host names.</summary>
        /// <param name="conversationId">The id the host gives the conversation. The vendor's conversation id belongs here.</param>
        /// <returns>The session of the new conversation, with no turn run yet.</returns>
        /// <exception cref="ArgumentException"><paramref name="conversationId"/> is null or empty.</exception>
        public ValueTask<AgentSession> CreateSessionAsync(string conversationId)
        {
            ArgumentException.ThrowIfNullOrEmpty(conversationId);
            return new(new AgentCoreAgentSession(_sessions.Create(conversationId)));
        }

        /// <inheritdoc />
        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
        {
            return new(new AgentCoreAgentSession(_sessions.Create()));
        }

        /// <inheritdoc />
        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(session);
            ConversationSession conversation = Resolve(session);

            return new(JsonSerializer.SerializeToElement(
                new SerializedSession(conversation.ConversationId, conversation.Snapshot(), EntryName),
                jsonSerializerOptions ?? ConversationStateJson.Options));
        }

        /// <inheritdoc />
        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement serializedState,
            JsonSerializerOptions? jsonSerializerOptions = null,
            CancellationToken cancellationToken = default)
        {
            SerializedSession? stored = serializedState.Deserialize<SerializedSession>(
                jsonSerializerOptions ?? ConversationStateJson.Options);

            if (stored is null or { ConversationId: null, State: null })
            {
                throw new ArgumentException(
                    "The serialized session names no conversation and holds no state, so it is not one this "
                    + "agent wrote. It expects { conversationId, state } — the conversation's id beside its state. A "
                    + "bare ConversationSessionState, the value store 0 keeps in conversation.state, is the other shape "
                    + "and reading it as this one would lose the conversation's transcript.",
                    nameof(serializedState));
            }

            if (stored.Entry is { Length: > 0 } entry && !string.Equals(entry, EntryName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"The session names entry '{stored.Entry}' and this agent serves entry '{EntryName}'.");
            }

            return new(new AgentCoreAgentSession(_sessions.Create(stored.ConversationId, stored.State)));
        }

        /// <inheritdoc />
        protected override async Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(messages);

            bool minted = session is null;
            session ??= await CreateSessionAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                TurnResult turn = await Resolve(session).RunTurnMessageAsync(UserMessage(messages), cancellationToken).ConfigureAwait(false);

                return new AgentResponse(new ChatMessage(ChatRole.Assistant, turn.ReplyText))
                {
                    AgentId = Id,
                    ResponseId = Guid.NewGuid().ToString("N"),
                    CreatedAt = turn.EndedAt,
                };
            }
            finally
            {
                if (minted)
                {
                    DisposeInBackground(Resolve(session));
                }
            }
        }

        /// <inheritdoc />
        protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(messages);

            return RunCoreStreamingCoreAsync(messages, session, cancellationToken);
        }

        /// <summary>Streams one turn, wrapping each update with this agent's id.</summary>
        private async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            bool minted = session is null;
            session ??= await CreateSessionAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                IAsyncEnumerable<ChatResponseUpdate> turn = Resolve(session).RunTurnMessageStreamingAsync(UserMessage(messages), cancellationToken);

                await foreach (ChatResponseUpdate? update in turn.ConfigureAwait(false))
                {
                    if (update.Contents.OfType<TurnCommittedContent>().Any())
                    {
                        continue;
                    }

                    yield return new AgentResponseUpdate(update) { AgentId = Id };
                }
            }
            finally
            {
                if (minted)
                {
                    DisposeInBackground(Resolve(session));
                }
            }
        }

        /// <summary>
        /// Starts disposing a minted session off the caller's path, and logs its outcome. Nothing here awaits
        /// the disposal, so an exception from it — including <see cref="Harness.BackgroundSessionRelease"/>'s timeout —
        /// can never mask the turn's own result or throw out of a run that already returned.
        /// </summary>
        private static void DisposeInBackground(ConversationSession conversation)
        {
            _ = Task.Run(() => DisposeLoggingFailureAsync(conversation));
        }

        private static async Task DisposeLoggingFailureAsync(ConversationSession conversation)
        {
            try
            {
                await conversation.DisposeAsync().ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Nothing awaits this disposal once the run returns, so nothing may escape it.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                Log.BackgroundReleaseFailed(conversation.Logger, conversation.ConversationId, exception);
            }
        }

        /// <summary>Finds the conversation a run belongs to.</summary>
        /// <param name="session">The session the caller passed.</param>
        /// <returns>The conversation to run the turn on.</returns>
        /// <exception cref="ArgumentException"><paramref name="session"/> belongs to another agent type.</exception>
        private static ConversationSession Resolve(AgentSession session)
        {
            return session switch
            {
                AgentCoreAgentSession own => own.Conversation,
                _ => throw new ArgumentException(
                    $"Incompatible session type: {session.GetType()} (expecting {typeof(AgentCoreAgentSession)}). "
                    + "Only a session this agent created can carry one of its conversations.",
                    nameof(session)),
            };
        }

        /// <summary>Reads what the caller said or answered out of the run's messages.</summary>
        /// <param name="messages">The messages the caller passed to the run.</param>
        /// <returns>The last user message that carries words or an approval answer.</returns>
        /// <exception cref="ArgumentException">No user message carries words or an answer, so there is no turn to run.</exception>
        private static ChatMessage UserMessage(IEnumerable<ChatMessage> messages)
        {
            ChatMessage? picked = null;

            foreach (ChatMessage message in messages)
            {
                if (message.Role == ChatRole.User
                    && (message.Text is { Length: > 0 }
                        || message.Contents.OfType<ToolApprovalResponseContent>().Any()))
                {
                    picked = message;
                }
            }

            return picked ?? throw new ArgumentException(
                "The run carries no user message with words or an approval answer, so there is no turn "
                + "to run. The session owns the transcript: pass what the caller just said, not a history.",
                nameof(messages));
        }

        /// <summary>One serialized session: the conversation it is, the entry it belongs to, and the state it held.</summary>
        /// <param name="ConversationId">The id of the conversation. Store 1 is keyed by it, so it is the half that finds the words.</param>
        /// <param name="State">What the session alone held, or <see langword="null"/> when the blob named none.</param>
        /// <param name="Entry">The entry that wrote the blob. A key minted by one entry never reads on another.</param>
        internal sealed record SerializedSession(string? ConversationId, ConversationSessionState? State, string? Entry);
    }
}
