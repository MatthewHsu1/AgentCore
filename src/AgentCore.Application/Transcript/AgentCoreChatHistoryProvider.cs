using AgentCore.Application.Conversation;
using AgentCore.Application.Conversation.Memory;
using AgentCore.Application.Ports;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentCore.Application.Transcript
{
    /// <summary>
    /// Store the words of a conversation, held for the session by the provider and written through to a backing store.
    /// </summary>
    internal sealed class AgentCoreChatHistoryProvider : ChatHistoryProvider
    {
        /// <summary>
        /// The state bag key that binds a session to its conversation. A session without it reads nothing, stages
        /// nothing and commits nothing, whatever else its bag carries.
        /// </summary>
        internal const string ConversationKey = "agentcore.history";

        private static readonly IReadOnlyList<string> Keys = [ConversationKey];

        private readonly ConversationGates _gates;

        private readonly ConversationWrites _writes;

        /// <summary>Creates the provider over a backing store.</summary>
        /// <param name="store">The message store, or <see langword="null"/> for one kept in this process.</param>
        /// <param name="logger">Where refused writes and cuts are logged.</param>
        public AgentCoreChatHistoryProvider(IConversationStore? store = null, ILogger? logger = null)
            : base(storeInputRequestMessageFilter: ExternalOnly)
        {
            ILogger log = logger ?? NullLogger.Instance;
            IConversationStore words = store ?? new InMemoryConversationStore();
            _gates = new ConversationGates(words, log);
            _writes = new ConversationWrites(_gates, words, log);
        }

        /// <inheritdoc />
        public override IReadOnlyList<string> StateKeys => Keys;

        /// <summary>
        /// Opens a conversation on one session: names it, reads back what it already said, and says where a
        /// dropped write is reported.
        /// </summary>
        /// <param name="session">The session this conversation runs on.</param>
        /// <param name="conversationId">The conversation being opened.</param>
        /// <param name="spoken">
        /// What the message store already holds for this conversation, which is empty for a conversation that is new. A second
        /// session of one conversation is handed the first session's words here, and nowhere else.
        /// </param>
        /// <param name="losses">Where a dropped message store write is counted, if anywhere.</param>
        /// <param name="marks">
        /// How far the conversation had got, from the conversation store's own counters — or the zero marks of a conversation that has
        /// never spoken. The words cannot say this on their own, because an edit deletes the rows that
        /// would otherwise answer for it.
        /// </param>
        /// <returns>The index the next turn of this conversation takes.</returns>
        public int BeginConversation(
            AgentSession session,
            string conversationId,
            IReadOnlyList<ConversationMessage> spoken,
            ITranscriptLossCounter? losses = null,
            TranscriptMarks marks = default)
        {
            ArgumentNullException.ThrowIfNull(session);
            ArgumentException.ThrowIfNullOrEmpty(conversationId);
            ArgumentNullException.ThrowIfNull(spoken);

            session.StateBag.SetValue(ConversationKey, conversationId);

            return UnderLock(
                session,
                (transcript, gate) =>
                {
                    transcript.ConversationId = conversationId;
                    gate.Losses = losses;
                    return transcript.Resume(spoken, marks);
                });
        }

        /// <summary>Reads where the session's words stand: the writes queued so far, the revision, and the next ordinal, together.</summary>
        /// <param name="session">The session this conversation runs on.</param>
        /// <returns>
        /// The position. Once <see cref="TranscriptPosition.Written"/> completes, the conversation store's counter equals
        /// <see cref="TranscriptPosition.NextOrdinal"/> unless a row was written by someone else, or one of this
        /// session's own writes was dropped.
        /// </returns>
        public TranscriptPosition Position(AgentSession session)
        {
            ArgumentNullException.ThrowIfNull(session);

            return UnderLock(
                session,
                static (transcript, gate) => new TranscriptPosition(gate.Writes, transcript.Revision, transcript.NextOrdinal));
        }

        /// <summary>
        /// Replaces the conversation's live history with what the message store holds, unless the session's words moved since
        /// the read began. Words that moved hold a write the message store may not have yet, and replacing them would lose it.
        /// </summary>
        /// <param name="session">The session this conversation runs on.</param>
        /// <param name="catchUp">What the message store held, and the revision the read began at.</param>
        /// <returns><see langword="false"/> when the words moved and nothing was replaced.</returns>
        public bool TryResync(AgentSession session, TranscriptCatchUp catchUp)
        {
            ArgumentNullException.ThrowIfNull(session);
            ArgumentNullException.ThrowIfNull(catchUp);

            return UnderLock(
                session,
                transcript =>
                {
                    if (transcript.Revision != catchUp.Revision)
                    {
                        return false;
                    }

                    transcript.Resync(catchUp.Rows, catchUp.NextOrdinal);
                    return true;
                });
        }

        /// <summary>Reads the summary the session holds in place of its oldest rows, or <see langword="null"/> when every row is live.</summary>
        /// <param name="session">The session this conversation runs on.</param>
        public ChatMessage? Summary(AgentSession session)
        {
            ArgumentNullException.ThrowIfNull(session);

            return UnderLock(session, static transcript => transcript.Summary?.Message);
        }

        /// <summary>Names where a compaction of the conversation would reach, and the revision it reads.</summary>
        /// <param name="session">The session this conversation runs on.</param>
        /// <returns>The floor, or <see langword="null"/> when the conversation holds nothing to compact.</returns>
        public TranscriptFloor? Floor(AgentSession session)
        {
            ArgumentNullException.ThrowIfNull(session);

            return UnderLock(session, static transcript => transcript.Floor());
        }

        /// <summary>
        /// Puts one summary in place of every row at or below <paramref name="coversUpTo"/>, unless the
        /// words moved since <see cref="Floor"/> was read, and queues the summary's row for the message store ahead
        /// of the turn's own words.
        /// </summary>
        /// <param name="session">The session this conversation runs on.</param>
        /// <param name="summary">The message that now speaks for those rows.</param>
        /// <param name="coversUpTo">The last ordinal it speaks for.</param>
        /// <param name="revision">The revision <see cref="Floor"/> reported.</param>
        /// <returns><see langword="true"/> when the summary now stands.</returns>
        public bool Compact(AgentSession session, ChatMessage summary, int coversUpTo, int revision)
        {
            ArgumentNullException.ThrowIfNull(session);
            ArgumentNullException.ThrowIfNull(summary);

            return _writes.Compact(session, summary, coversUpTo, revision);
        }

        /// <summary>
        /// Whether <see cref="TruncateFromAsync"/> can cut an edit hanging off one message from what the session
        /// holds. It cannot when the cut would reach a row the summary stands in for; every row must be read back first.
        /// </summary>
        /// <param name="session">The session this conversation runs on.</param>
        /// <param name="parentMessageId">The message the edit hangs off, or <see langword="null"/> for the whole conversation.</param>
        public bool CanTruncateFrom(AgentSession session, string? parentMessageId)
        {
            ArgumentNullException.ThrowIfNull(session);

            return UnderLock(session, transcript => transcript.CanTruncateFrom(parentMessageId));
        }

        /// <summary>
        /// Stamps the turn the next run belongs to, and drops anything an earlier turn staged and never committed.
        /// </summary>
        public void BeginTurn(AgentSession session, int turnIndex)
        {
            ArgumentNullException.ThrowIfNull(session);

            _ = UnderLock(
                session,
                (transcript, gate) =>
                {
                    transcript.BeginTurn(turnIndex);
                    gate.Staged.Clear();
                    gate.Replay.Clear();
                    return true;
                });
        }

        /// <summary>
        /// Reads the conversation as the model sees it: the summary first, when one stands, then every live row, oldest first.
        /// </summary>
        public IReadOnlyList<ChatMessage> Read(AgentSession session)
        {
            ArgumentNullException.ThrowIfNull(session);

            return UnderLock(session, static transcript => transcript.Read());
        }

        /// <summary>Reads what the framework staged for the running turn and no commit has taken yet, oldest first.</summary>
        /// <param name="session">The session this conversation runs on.</param>
        public IReadOnlyList<ChatMessage> Staged(AgentSession session)
        {
            ArgumentNullException.ThrowIfNull(session);

            return UnderLock(session, static (_, gate) => (IReadOnlyList<ChatMessage>)[.. gate.Staged]);
        }

        /// <summary>
        /// Seals one turn: the one durable append of that turn, holding its words and the state that follows
        /// them. Whatever the framework staged for the turn is taken here, and nothing stays staged.
        /// </summary>
        /// <param name="session">The session this conversation runs on.</param>
        /// <param name="commit">The turn being sealed.</param>
        /// <returns>
        /// What the user's message and the last message were written under, and whether the store kept them, or
        /// <see langword="null"/> when the session is bound to no conversation and nothing was written. The reply id
        /// is <see langword="null"/> when the user's message is all the turn wrote.
        /// </returns>
        public TurnWrite? CommitTurn(AgentSession session, TurnCommit commit)
        {
            ArgumentNullException.ThrowIfNull(session);
            ArgumentNullException.ThrowIfNull(commit);

            return IsBound(session) ? _writes.Commit(session, commit) : null;
        }

        /// <summary>Appends rows between turns, under the turn the conversation takes next. An unbound session writes nothing.</summary>
        public void AppendBetweenTurns(AgentSession session, IReadOnlyList<ChatMessage> messages, int nextTurnIndex)
        {
            if (IsBound(session))
            {
                _writes.AppendBetweenTurns(session, messages, nextTurnIndex);
            }
        }

        /// <summary>Queues the write of a state no turn's words carry, behind every write already queued. An unbound session writes nothing.</summary>
        public void SaveState(AgentSession session, ConversationSessionState state)
        {
            if (IsBound(session))
            {
                _writes.SaveState(session, state);
            }
        }

        /// <summary>Withdraws everything the conversation said after one message, because a caller replaced it.</summary>
        /// <param name="session">The session this conversation runs on.</param>
        /// <param name="parentMessageId">
        /// The message the caller's new words hang off. Everything after it goes. Pass
        /// <see langword="null"/> to withdraw the whole conversation, which is what an edit of its first message
        /// asks for.
        /// </param>
        /// <param name="cancellationToken">Cancels the read that judges the session's lost writes before the cut.</param>
        /// <returns>
        /// The turns the withdrawal took, or <see langword="null"/> when nothing went. Nothing goes when
        /// the conversation holds no message of that name — a caller naming a message this host never stored —
        /// and the turn then runs as a plain new turn.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// The cut reaches a row the summary stands in for. Ask <see cref="CanTruncateFrom"/> first, and read every
        /// row back when it says no.
        /// </exception>
        public ValueTask<WithdrawnTurns?> TruncateFromAsync(AgentSession session, string? parentMessageId, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(session);

            return _writes.TruncateFromAsync(session, parentMessageId, cancellationToken);
        }

        /// <summary>
        /// Reads the live rows an edit hanging off one message would withdraw: every row after it, or every live row when
        /// the session does not hold it, as an edit under the summary reads them.
        /// </summary>
        /// <param name="session">The session this conversation runs on.</param>
        /// <param name="parentMessageId">The message the edit hangs off, or <see langword="null"/> for the whole conversation.</param>
        /// <returns>The rows, oldest first, and whether the session holds the parent (always, for the whole conversation).</returns>
        public (IReadOnlyList<ChatMessage> Rows, bool Held) RowsAfter(AgentSession session, string? parentMessageId)
        {
            ArgumentNullException.ThrowIfNull(session);

            return UnderLock(session, transcript => (transcript.After(parentMessageId), parentMessageId is null || transcript.OrdinalOf(parentMessageId) is not null));
        }

        /// <summary>
        /// Withdraws an edit that reaches under the summary: the parent is a row the summary stands in for, or the
        /// edit takes the whole conversation. The store cuts the rows, and the session reads every row back.
        /// Ask <see cref="CanTruncateFrom"/> first, and come here when it says no.
        /// </summary>
        /// <param name="session">The session this conversation runs on.</param>
        /// <param name="parentMessageId">The message the edit hangs off, or <see langword="null"/> for the whole conversation.</param>
        /// <param name="cancellationToken">Cancels the cut.</param>
        /// <returns>The turns the cut withdrew, or <see langword="null"/> when nothing went.</returns>
        public ValueTask<WithdrawnTurns?> CutUnderSummaryAsync(
            AgentSession session, string? parentMessageId, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(session);

            return _writes.CutUnderSummaryAsync(session, parentMessageId, cancellationToken);
        }

        /// <summary>Rewrites the reply of a turn already committed to the text the user was shown. It never appends.</summary>
        /// <param name="session">The session this conversation runs on.</param>
        /// <param name="turnIndex">The turn whose reply is rewritten. It must be the turn of the conversation's last reply.</param>
        /// <param name="shown">The text the user saw or heard.</param>
        /// <returns><see langword="false"/> when the last reply belongs to another turn, or there is none.</returns>
        public bool RewriteReply(AgentSession session, int turnIndex, string shown)
        {
            ArgumentNullException.ThrowIfNull(session);
            ArgumentNullException.ThrowIfNull(shown);

            return _writes.Rewrite(session, turnIndex, shown);
        }

        /// <summary>
        /// Waits for every write this conversation has queued to reach the store.
        /// </summary>
        public Task DrainAsync(AgentSession session)
        {
            ArgumentNullException.ThrowIfNull(session);

            return _gates.Writes(session);
        }

        /// <inheritdoc />
        protected override ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(
            InvokingContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            return new(context.Session is { } session && IsBound(session)
                ? UnderLock(session, static (transcript, gate) => gate.Replay.Count == 0 ? transcript.Read() : [.. transcript.Read(), .. gate.Replay])
                : []);
        }

        /// <summary>
        /// Stages the run's response for the turn. It writes nothing durable: the hook runs before the stage
        /// machine and the fallback layer, so the words and their state are written together by
        /// <see cref="CommitTurn"/>. Staging appends. The first run of a turn reads the conversation only; a later
        /// run of the same turn (a <c>loop:</c> round, an AfterRun continue, an approval re-entry) also reads what the
        /// earlier runs were told and answered, which is never committed beyond the staged response.
        /// </summary>
        protected override ValueTask StoreChatHistoryAsync(
            InvokedContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            if (context.Session is { } session && IsBound(session) && context.ResponseMessages is { } response)
            {
                _ = UnderLock(
                    session,
                    (_, gate) =>
                    {
                        gate.Staged.AddRange(response);
                        gate.Replay.AddRange(ExternalOnly(context.RequestMessages).Select(Detached));
                        gate.Replay.AddRange(response.Select(Detached));
                        return true;
                    });
            }

            return default;
        }

        private static bool IsBound(AgentSession session)
        {
            return session.StateBag.TryGetValue(ConversationKey, out string? _);
        }

        // The user row comes from the turn, so the hook keeps only what the caller sent: context providers'
        // system lines (clock, reminders, skills) arrive in the request too.
        private static IEnumerable<ChatMessage> ExternalOnly(IEnumerable<ChatMessage> messages)
        {
            return messages.Where(message => message.GetAgentRequestMessageSourceType() == AgentRequestMessageSourceType.External);
        }

        // MAF tags each message it reads from this provider in the message's property bag. A replayed message shares
        // nothing with the staged one, so the tag never reaches a stored row.
        private static ChatMessage Detached(ChatMessage message)
        {
            ChatMessage copy = message.Clone();
            copy.AdditionalProperties = message.AdditionalProperties?.Clone();
            return copy;
        }

        private TResult UnderLock<TResult>(AgentSession session, Func<ConversationTranscript, ConversationGate, TResult> work)
        {
            return _gates.Under(session, work);
        }

        private TResult UnderLock<TResult>(AgentSession session, Func<ConversationTranscript, TResult> work)
        {
            return UnderLock(session, (transcript, _) => work(transcript));
        }
    }
}
