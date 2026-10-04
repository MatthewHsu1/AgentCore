using AgentCore.Application.Conversation;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime.Turn;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using AgentCore.Domain;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.Application.Runtime
{
    /// <summary>
    /// The whole turn loop of this library, behind the framework's own agent abstraction.
    /// </summary>
    public sealed class AgentCoreAgent : AIAgent
    {
        private readonly IConversationSessions _sessions;

        private readonly string? _description;

        /// <summary>Creates the shim over one compiled entry's turn loop.</summary>
        /// <param name="sessions">The owner that holds this entry's conversations live, shared by every entry.</param>
        /// <param name="entryName">The entry this agent serves. Reported as <see cref="Name"/>.</param>
        /// <param name="description">The description the agent reports, or <see langword="null"/>.</param>
        /// <exception cref="ArgumentNullException"><paramref name="sessions"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="entryName"/> is null or empty.</exception>
        public AgentCoreAgent(IConversationSessions sessions, string entryName, string? description = null)
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

        /// <summary>Starts, or resumes, one conversation under the id the host names.</summary>
        /// <param name="conversationId">The id the host gives the conversation. The vendor's conversation id belongs here.</param>
        /// <param name="cancellationToken">Cancels the open.</param>
        /// <returns>The live session of the conversation: the one this entry already holds, or a freshly opened one.</returns>
        /// <exception cref="ArgumentException"><paramref name="conversationId"/> is null or empty.</exception>
        /// <exception cref="ConversationInUseException">Another entry holds this id live.</exception>
        public async ValueTask<AgentSession> CreateSessionAsync(string conversationId, CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrEmpty(conversationId);

            ConversationSession conversation = await _sessions
                .GetOrOpenAsync(EntryName, conversationId, state: null, cancellationToken)
                .ConfigureAwait(false);

            return new AgentCoreAgentSession(conversation);
        }

        /// <inheritdoc />
        protected override async ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
        {
            ConversationSession conversation = await _sessions
                .GetOrOpenAsync(EntryName, conversationId: null, state: null, cancellationToken)
                .ConfigureAwait(false);

            return new AgentCoreAgentSession(conversation);
        }

        /// <inheritdoc />
        protected override async ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(session);
            ConversationSession conversation = ResolveOwn(session).Conversation;

            // A session this entry no longer holds live was unloaded: the conversation store's own copy of its state
            // outranks whatever this blob would otherwise carry, so the state travels as null.
            ConversationSession? live = await _sessions
                .TryGetAsync(EntryName, conversation.ConversationId, cancellationToken)
                .ConfigureAwait(false);

            return JsonSerializer.SerializeToElement(
                new SerializedSession(conversation.ConversationId, live?.States.Snapshot(), EntryName),
                jsonSerializerOptions ?? ConversationStateJson.Options);
        }

        /// <inheritdoc />
        protected override async ValueTask<AgentSession> DeserializeSessionCoreAsync(
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
                    + "bare ConversationSessionState, the value the conversation store keeps in conversation.state, is the other shape "
                    + "and reading it as this one would lose the conversation's transcript.",
                    nameof(serializedState));
            }

            ConversationSession conversation = await _sessions
                .GetOrOpenAsync(EntryName, stored.ConversationId, stored.State, cancellationToken)
                .ConfigureAwait(false);

            return new AgentCoreAgentSession(conversation);
        }

        /// <inheritdoc />
        protected override async Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(messages);

            AgentCoreAgentSession own = session is null
                ? (AgentCoreAgentSession)await CreateSessionAsync(cancellationToken).ConfigureAwait(false)
                : await ResolveLiveAsync(session, cancellationToken).ConfigureAwait(false);

            TurnResult turn = await own.Conversation.RunTurnMessageAsync(UserMessage(messages), cancellationToken).ConfigureAwait(false);

            return new AgentResponse(new ChatMessage(ChatRole.Assistant, turn.ReplyText))
            {
                AgentId = Id,
                ResponseId = Guid.NewGuid().ToString("N"),
                CreatedAt = turn.EndedAt,
            };
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
            AgentCoreAgentSession own = session is null
                ? (AgentCoreAgentSession)await CreateSessionAsync(cancellationToken).ConfigureAwait(false)
                : await ResolveLiveAsync(session, cancellationToken).ConfigureAwait(false);

            IAsyncEnumerable<ChatResponseUpdate> turn = own.Conversation.RunTurnMessageStreamingAsync(UserMessage(messages), cancellationToken);

            await foreach (ChatResponseUpdate? update in turn.ConfigureAwait(false))
            {
                if (update.Contents.OfType<TurnCommittedContent>().Any())
                {
                    continue;
                }

                yield return new AgentResponseUpdate(update) { AgentId = Id };
            }
        }

        /// <summary>
        /// Holds a session's live conversation for one host request, as <see cref="ConversationBusyMark.EnterRequestAsync"/>
        /// does. A conversation closed while the request waited is asked for again through the owner, and the request
        /// waits on the new session instead, so it never runs on a disposed one. Pair it with one
        /// <see cref="ConversationBusyMark.ExitRequestAsync"/> on the conversation it returns.
        /// </summary>
        /// <param name="session">A session this agent created.</param>
        /// <param name="cancellationToken">Cancels the waits.</param>
        /// <returns>The live conversation now held; the session's own <c>GetService</c> answers it too.</returns>
        /// <exception cref="Conversation.ConversationTurnConflictException">The conversation stayed busy past the wait limit.</exception>
        internal async ValueTask<ConversationSession> EnterRequestAsync(AgentSession session, CancellationToken cancellationToken)
        {
            AgentCoreAgentSession own = ResolveOwn(session);
            ConversationSession held = own.Conversation;
            long started = held.Time.GetTimestamp();

            while (true)
            {
                await held.Busy.EnterRequestAsync(started, cancellationToken).ConfigureAwait(false);

                ConversationSession live;
                try
                {
                    live = await own.ResolveAsync(_sessions, EntryName, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    await held.Busy.ExitRequestAsync().ConfigureAwait(false);
                    throw;
                }

                if (ReferenceEquals(live, held))
                {
                    return held;
                }

                await held.Busy.ExitRequestAsync().ConfigureAwait(false);
                held = live;
            }
        }

        /// <summary>Finds the conversation a run belongs to.</summary>
        /// <param name="session">The session the caller passed.</param>
        /// <returns>The conversation to run the turn on.</returns>
        /// <exception cref="ArgumentException"><paramref name="session"/> belongs to another agent type.</exception>
        private static AgentCoreAgentSession ResolveOwn(AgentSession session)
        {
            return session switch
            {
                AgentCoreAgentSession own => own,
                _ => throw new ArgumentException(
                    $"Incompatible session type: {session.GetType()} (expecting {typeof(AgentCoreAgentSession)}). "
                    + "Only a session this agent created can carry one of its conversations.",
                    nameof(session)),
            };
        }

        /// <summary>
        /// Resolves a caller-supplied session's live conversation through the owner, so the run touches its
        /// idle clock and a session that was unloaded since the caller last used it comes back rebuilt, with
        /// its history intact, rather than running a turn on a disposed instance.
        /// </summary>
        private async ValueTask<AgentCoreAgentSession> ResolveLiveAsync(AgentSession session, CancellationToken cancellationToken)
        {
            AgentCoreAgentSession own = ResolveOwn(session);
            _ = await own.ResolveAsync(_sessions, EntryName, cancellationToken).ConfigureAwait(false);
            return own;
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

        /// <summary>One serialized session: the conversation it is, the entry that wrote it, and the state it held.</summary>
        /// <param name="ConversationId">The id of the conversation. The message store is keyed by it, so it is the half that finds the words.</param>
        /// <param name="State">What the session alone held, or <see langword="null"/> when the blob named none.</param>
        /// <param name="Entry">
        /// The entry that wrote the blob. Written on every serialize and never read back: any entry may resume a
        /// conversation from a blob another entry wrote.
        /// </param>
        internal sealed record SerializedSession(string? ConversationId, ConversationSessionState? State, string? Entry);
    }
}
