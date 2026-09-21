using System.Runtime.CompilerServices;
using AgentCore.Application.Conversation;
using AgentCore.Application.Conversation.Memory;
using AgentCore.Application.Diagnostics;
using AgentCore.Application.Ports;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentCore.Application.Transcript
{
    /// <summary>
    /// Reports one store 1 write that was dropped, so the conversation can raise a diagnostic for it.
    /// </summary>
    internal delegate void TranscriptWriteDropped(int turnIndex, Exception exception);

    /// <summary>
    /// Store 1: the words of a conversation, held for the session by the provider and written through to a backing store.
    /// </summary>
    /// <remarks>Creates the provider over a backing store.</remarks>
    internal sealed class AgentCoreChatHistoryProvider(IConversationStore? store = null, ILogger? logger = null) : ChatHistoryProvider
    {
        private readonly ConditionalWeakTable<AgentSession, ConversationGate> _gates = [];

        private readonly IConversationStore _store = store ?? new InMemoryConversationStore();

        private readonly ILogger _logger = logger ?? NullLogger.Instance;

        /// <summary>
        /// Opens a conversation on one session: names it, reads back what it already said, and says where a
        /// dropped write is reported.
        /// </summary>
        /// <param name="session">The session this conversation runs on.</param>
        /// <param name="conversationId">The conversation being opened.</param>
        /// <param name="spoken">
        /// What store 1 already holds for this conversation, which is empty for a conversation that is new. A second
        /// session of one conversation is handed the first session's words here, and nowhere else.
        /// </param>
        /// <param name="report">Where a dropped store 1 write is reported, if anywhere.</param>
        /// <param name="marks">
        /// How far the conversation had got, from store 0's own counters — or the zero marks of a conversation that has
        /// never spoken. The words cannot say this on their own, because an edit deletes the rows that
        /// would otherwise answer for it.
        /// </param>
        /// <returns>The index the next turn of this conversation takes.</returns>
        public int BeginConversation(
            AgentSession session,
            string conversationId,
            IReadOnlyList<ConversationMessage> spoken,
            TranscriptWriteDropped? report = null,
            TranscriptMarks marks = default)
        {
            ArgumentNullException.ThrowIfNull(session);
            ArgumentException.ThrowIfNullOrEmpty(conversationId);
            ArgumentNullException.ThrowIfNull(spoken);

            return UnderLock(
                session,
                (transcript, gate) =>
                {
                    transcript.ConversationId = conversationId;
                    gate.Dropped = report;
                    return transcript.Resume(spoken, marks);
                });
        }

        /// <summary>Reads the ordinal the session expects the conversation's next row to take.</summary>
        /// <param name="session">The session this conversation runs on.</param>
        /// <returns>
        /// The next free ordinal as the session counts it. Equal to store 0's own counter unless a row was
        /// written by someone else, or one of this session's own writes was dropped.
        /// </returns>
        public int NextOrdinal(AgentSession session)
        {
            ArgumentNullException.ThrowIfNull(session);

            return UnderLock(session, static transcript => transcript.NextOrdinal);
        }

        /// <summary>Replaces the conversation's live history with what store 1 holds.</summary>
        /// <param name="session">The session this conversation runs on.</param>
        /// <param name="rows">The rows store 1 read for the session, as <see cref="ConversationTranscript.Resync"/> takes them.</param>
        /// <param name="nextOrdinal">The next free ordinal of the conversation, from store 0's own counter.</param>
        public void Resync(AgentSession session, IReadOnlyList<ConversationMessage> rows, int nextOrdinal)
        {
            ArgumentNullException.ThrowIfNull(session);
            ArgumentNullException.ThrowIfNull(rows);

            _ = UnderLock(
                session,
                transcript =>
                {
                    transcript.Resync(rows, nextOrdinal);
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
        /// words moved since <see cref="Floor"/> was read, and queues the summary's row for store 1 ahead
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

            return UnderLock(
                session,
                (transcript, gate) =>
                {
                    if (transcript.Compact(summary, coversUpTo, revision) is not { } row)
                    {
                        return false;
                    }

                    ConversationMessageDraft draft = new(row.TurnIndex, row.Content, row.MessageId) { CoversUpTo = row.CoversUpTo };
                    gate.Enqueue(() => new ValueTask(_store.AppendAsync(
                        transcript.ConversationId, [draft], state: null, CancellationToken.None).AsTask()));

                    return true;
                });
        }

        /// <summary>
        /// Whether <see cref="TruncateFrom"/> can cut an edit hanging off one message from what the session
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
        /// Stamps the turn the next run belongs to.
        /// </summary>
        public void BeginTurn(AgentSession session, int turnIndex)
        {
            ArgumentNullException.ThrowIfNull(session);

            _ = UnderLock(
                session,
                transcript =>
                {
                    transcript.BeginTurn(turnIndex);
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

        /// <summary>Adds one finished turn's messages to the conversation, and the state that follows them.</summary>
        /// <param name="session">The session this conversation runs on.</param>
        /// <param name="messages">The messages the turn produced.</param>
        /// <param name="state">
        /// The state to store beside the words, or <see langword="null"/> to store none. A value, read
        /// by the caller before it conversations: everything the turn writes to the state document — the clock
        /// fields, the counters, the stage and whether the machine finished — is already final when the
        /// caller enters its commit lock, and the late barge-in path that runs after this conversation amends
        /// the record of the turn without touching any of it. So there is nothing later to wait for.
        /// </param>
        /// <param name="firstMessageId">
        /// What the caller calls the first of these messages, or <see langword="null"/> to name it in the
        /// append. Only the first: the rest are this conversation's own words, which no caller had a name for.
        /// </param>
        /// <returns>
        /// The name the last of these messages was written under, so the caller can be told what to hang
        /// its next edit off. It is <see langword="null"/> only when nothing was written.
        /// </returns>
        public string? AppendTurn(
            AgentSession session,
            IReadOnlyList<ChatMessage> messages,
            ConversationSessionState? state = null,
            string? firstMessageId = null)
        {
            ArgumentNullException.ThrowIfNull(session);
            ArgumentNullException.ThrowIfNull(messages);

            return messages.Count == 0
                ? null
                : UnderLock(
                session,
                (transcript, gate) =>
                {
                    IReadOnlyList<ConversationMessage> rows = transcript.Append(messages, firstMessageId);

                    ConversationMessageDraft[] drafts = [.. rows.Select(row => new ConversationMessageDraft(row.TurnIndex, row.Content, row.MessageId))];

                    gate.Enqueue(() => new ValueTask(_store.AppendAsync(
                        transcript.ConversationId, drafts, state, CancellationToken.None).AsTask()));
                    return rows[^1].MessageId;
                });
        }

        /// <summary>Withdraws everything the conversation said after one message, because a caller replaced it.</summary>
        /// <param name="session">The session this conversation runs on.</param>
        /// <param name="parentMessageId">
        /// The message the caller's new words hang off. Everything after it goes. Pass
        /// <see langword="null"/> to withdraw the whole conversation, which is what an edit of its first message
        /// asks for.
        /// </param>
        /// <returns>
        /// The turns the withdrawal took, or <see langword="null"/> when nothing went. Nothing goes when
        /// the conversation holds no message of that name — a caller naming a message this host never stored —
        /// and the turn then runs as a plain new turn.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// The cut reaches a row the summary stands in for. Ask <see cref="CanTruncateFrom"/> first, and read every
        /// row back when it says no.
        /// </exception>
        public WithdrawnTurns? TruncateFrom(AgentSession session, string? parentMessageId)
        {
            ArgumentNullException.ThrowIfNull(session);

            return UnderLock(
                session,
                (transcript, gate) =>
                {
                    int from;
                    if (parentMessageId is null)
                    {
                        from = 0;
                    }
                    else if (transcript.OrdinalOf(parentMessageId) is { } parent)
                    {
                        from = parent + 1;
                    }
                    else
                    {
                        return (WithdrawnTurns?)null;
                    }

                    if (transcript.TruncateFrom(from) is not { } withdrawn)
                    {
                        return null;
                    }

                    Log.ConversationTruncated(_logger, transcript.ConversationId, from, transcript.TurnIndex);

                    gate.Enqueue(() => new ValueTask(
                        _store.TruncateAsync(transcript.ConversationId, from, CancellationToken.None).AsTask()));

                    return withdrawn;
                });
        }

        /// <summary>Adds the caller-facing turn of a graph row: what the caller said, and what it heard.</summary>
        /// <param name="session">The session this conversation runs on.</param>
        /// <param name="spoken">What the caller said.</param>
        /// <param name="heard">What the caller heard, or <see langword="null"/> when it heard nothing.</param>
        /// <param name="state">
        /// The state to store beside the words, on the same terms as
        /// <see cref="AppendTurn(AgentSession, IReadOnlyList{ChatMessage}, ConversationSessionState?, string?)"/>.
        /// </param>
        /// <param name="firstMessageId">What the caller calls the message it sent, if it named one.</param>
        /// <inheritdoc cref="AppendTurn(AgentSession, IReadOnlyList{ChatMessage}, ConversationSessionState?, string?)" path="/returns"/>
        public string? AppendCallerFacingTurn(
            AgentSession session,
            ChatMessage spoken,
            ChatMessage? heard,
            ConversationSessionState? state = null,
            string? firstMessageId = null)
        {
            ArgumentNullException.ThrowIfNull(spoken);

            return AppendTurn(session, heard is null ? [spoken] : [spoken, heard], state, firstMessageId);
        }

        /// <summary>
        /// Replaces the reply the caller was hearing with the words the caller actually heard.
        /// </summary>
        public bool TruncateLastReply(AgentSession session, string heard, TimeSpan played)
        {
            ArgumentNullException.ThrowIfNull(session);
            ArgumentNullException.ThrowIfNull(heard);

            return UnderLock(
                session,
                (transcript, gate) =>
                {
                    IReadOnlyList<ConversationMessage> rows = transcript.TruncateLastReply(heard);
                    if (rows.Count == 0)
                    {
                        return false;
                    }

                    Log.ReplyTruncated(_logger, rows[0].ConversationId, rows[0].TurnIndex, played.TotalMilliseconds);

                    foreach (ConversationMessage row in rows)
                    {
                        gate.Enqueue(() => _store.RewriteAsync(row.ConversationId, row.MessageId, row.Content, CancellationToken.None));
                    }

                    return true;
                });
        }

        /// <summary>
        /// Waits for every write this conversation has queued to reach the store.
        /// </summary>
        public Task DrainAsync(AgentSession session)
        {
            ArgumentNullException.ThrowIfNull(session);

            ConversationGate gate = GateFor(session);
            lock (gate.Sync)
            {
                return gate.Writes;
            }
        }

        /// <inheritdoc />
        protected override ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(
            InvokingContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            return new(context.Session is { } session ? Read(session) : []);
        }

        /// <inheritdoc />
        protected override ValueTask StoreChatHistoryAsync(
            InvokedContext context, CancellationToken cancellationToken = default)
        {
            return default;
        }

        /// <summary>Runs one piece of work against the conversation's transcript, alone.</summary>
        private TResult UnderLock<TResult>(AgentSession session, Func<ConversationTranscript, ConversationGate, TResult> work)
        {
            ConversationGate gate = GateFor(session);

            lock (gate.Sync)
            {
                return work(gate.Transcript, gate);
            }
        }

        private TResult UnderLock<TResult>(AgentSession session, Func<ConversationTranscript, TResult> work)
        {
            return UnderLock(session, (transcript, _) => work(transcript));
        }

        private ConversationGate GateFor(AgentSession session)
        {
            return _gates.GetValue(session, _ => new ConversationGate(_logger));
        }
    }
}
