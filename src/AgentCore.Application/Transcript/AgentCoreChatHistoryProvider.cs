using System.Runtime.CompilerServices;
using AgentCore.Application.Calls;
using AgentCore.Application.Calls.Memory;
using AgentCore.Application.Diagnostics;
using AgentCore.Application.Ports;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentCore.Application.Transcript;

/// <summary>
/// Reports one store 1 write that was dropped, so the call can raise a diagnostic for it.
/// </summary>
internal delegate void TranscriptWriteDropped(int turnIndex, Exception exception);

/// <summary>
/// Store 1: the words of a call, held by the session and written through to a backing store.
/// </summary>
internal sealed class AgentCoreChatHistoryProvider : ChatHistoryProvider
{
    private readonly ConditionalWeakTable<AgentSession, CallGate> _gates = [];

    private readonly ProviderSessionState<CallTranscript> _state;

    private readonly ICallStore _store;

    private readonly ILogger _logger;

    /// <summary>Creates the provider over a backing store.</summary>
    public AgentCoreChatHistoryProvider(ICallStore? store = null, ILogger? logger = null)
    {
        _store = store ?? new InMemoryCallStore();

        _logger = logger ?? NullLogger.Instance;

        _state = new(static _ => new CallTranscript(), StateKeys[0], TranscriptJson.Options);
    }

    /// <summary>
    /// Opens a call on one session: names it, reads back what it already said, and says where a
    /// dropped write is reported.
    /// </summary>
    /// <param name="session">The session this call runs on.</param>
    /// <param name="callId">The call being opened.</param>
    /// <param name="spoken">
    /// What store 1 already holds for this call, which is empty for a call that is new. A second
    /// session of one call is handed the first session's words here, and nowhere else.
    /// </param>
    /// <param name="report">Where a dropped store 1 write is reported, if anywhere.</param>
    /// <param name="stored">
    /// The state kept beside these words, or <see langword="null"/> for a call that has none because
    /// it has never spoken. Its marks say how far the call had got; the words cannot, because an edit
    /// deletes the rows that would otherwise answer for it.
    /// </param>
    /// <returns>The index the next turn of this call takes.</returns>
    public int BeginCall(
        AgentSession session,
        string callId,
        IReadOnlyList<CallMessage> spoken,
        TranscriptWriteDropped? report = null,
        CallSessionState? stored = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrEmpty(callId);
        ArgumentNullException.ThrowIfNull(spoken);

        var marks = stored is null
            ? default
            : new TranscriptMarks(stored.NextOrdinal, stored.NextTurnIndex);

        return UnderLock(
            session,
            (transcript, gate) =>
            {
                transcript.CallId = callId;
                gate.Dropped = report;
                return transcript.Resume(spoken, marks);
            });
    }

    /// <summary>
    /// Stamps the turn the next run belongs to.
    /// </summary>
    public void BeginTurn(AgentSession session, int turnIndex)
    {
        ArgumentNullException.ThrowIfNull(session);

        UnderLock(
            session,
            transcript =>
            {
                transcript.BeginTurn(turnIndex);
                return true;
            });
    }

    /// <summary>
    /// Reads the whole call, oldest message first.
    /// </summary>
    public IReadOnlyList<ChatMessage> Read(AgentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        return UnderLock(session, static transcript => transcript.Read());
    }

    /// <summary>Reads the next free ordinal of the call on one session.</summary>
    /// <param name="session">The session this call runs on.</param>
    /// <returns>The ordinal the call's next row takes.</returns>
    public int NextOrdinal(AgentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        return UnderLock(session, static transcript => transcript.NextOrdinal);
    }

    /// <summary>Adds one finished turn's messages to the call, and the state that follows them.</summary>
    /// <param name="session">The session this call runs on.</param>
    /// <param name="messages">The messages the turn produced.</param>
    /// <param name="state">
    /// The state to store beside the words, or <see langword="null"/> to store none. A value, read
    /// by the caller before it calls: everything the turn writes to the state document — the clock
    /// fields, the counters, the stage and whether the machine finished — is already final when the
    /// caller enters its commit lock, and the late barge-in path that runs after this call amends
    /// the record of the turn without touching any of it. So there is nothing later to wait for.
    /// </param>
    /// <param name="firstMessageId">
    /// What the caller calls the first of these messages, or <see langword="null"/> to name it in the
    /// append. Only the first: the rest are this call's own words, which no caller had a name for.
    /// </param>
    /// <returns>
    /// The name the last of these messages was written under, so the caller can be told what to hang
    /// its next edit off. It is <see langword="null"/> only when nothing was written.
    /// </returns>
    public string? AppendTurn(
        AgentSession session,
        IReadOnlyList<ChatMessage> messages,
        CallSessionState? state = null,
        string? firstMessageId = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(messages);

        if (messages.Count == 0)
        {
            return null;
        }

        return UnderLock(
            session,
            (transcript, gate) =>
            {
                var rows = transcript.Append(messages, firstMessageId);

                // After the append, never before. The caller reads the state on its own thread and
                // hands it in — which is right for everything else it carries — but the ordinal this
                // turn leaves behind is not known until the rows are cut, and a state that named the
                // ordinal from before them would resume the call one turn short and overwrite them.
                var stored = state is null ? null : state with { NextOrdinal = transcript.NextOrdinal };

                Enqueue(gate, () => _store.AppendAsync(rows, stored, CancellationToken.None), transcript);
                return rows[^1].MessageId;
            });
    }

    /// <summary>Withdraws everything the call said after one message, because a caller replaced it.</summary>
    /// <param name="session">The session this call runs on.</param>
    /// <param name="parentMessageId">
    /// The message the caller's new words hang off. Everything after it goes. Pass
    /// <see langword="null"/> to withdraw the whole call, which is what an edit of its first message
    /// asks for.
    /// </param>
    /// <returns>
    /// The turns the withdrawal took, or <see langword="null"/> when nothing went. Nothing goes when
    /// the call holds no message of that name — a caller naming a message this host never stored —
    /// and the turn then runs as a plain new turn.
    /// </returns>
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

                Log.CallTruncated(_logger, transcript.CallId, from, transcript.TurnIndex);

                Enqueue(
                    gate,
                    () => new ValueTask(
                        _store.TruncateAsync(transcript.CallId, from, CancellationToken.None).AsTask()),
                    transcript);

                return withdrawn;
            });
    }

    /// <summary>Adds the caller-facing turn of a graph row: what the caller said, and what it heard.</summary>
    /// <param name="session">The session this call runs on.</param>
    /// <param name="spoken">What the caller said.</param>
    /// <param name="heard">What the caller heard, or <see langword="null"/> when it heard nothing.</param>
    /// <param name="state">
    /// The state to store beside the words, on the same terms as
    /// <see cref="AppendTurn(AgentSession, IReadOnlyList{ChatMessage}, CallSessionState?, string?)"/>.
    /// </param>
    /// <param name="firstMessageId">What the caller calls the message it sent, if it named one.</param>
    /// <inheritdoc cref="AppendTurn(AgentSession, IReadOnlyList{ChatMessage}, CallSessionState?, string?)" path="/returns"/>
    public string? AppendCallerFacingTurn(
        AgentSession session,
        ChatMessage spoken,
        ChatMessage? heard,
        CallSessionState? state = null,
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
                var rows = transcript.TruncateLastReply(heard);
                if (rows.Count == 0)
                {
                    return false;
                }

                Log.ReplyTruncated(_logger, rows[0].CallId, rows[0].TurnIndex, played.TotalMilliseconds);

                foreach (var row in rows)
                {
                    Enqueue(
                        gate,
                        () => _store.RewriteAsync(row.CallId, row.Ordinal, row.Content, CancellationToken.None),
                        transcript);
                }

                return true;
            });
    }

    /// <summary>
    /// Waits for every write this call has queued to reach the store.
    /// </summary>
    public Task DrainAsync(AgentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var gate = GateFor(session);
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
        InvokedContext context, CancellationToken cancellationToken = default) => default;

    /// <summary>Queues one store write behind everything this call has already queued.</summary>
    private void Enqueue(CallGate gate, Func<ValueTask> write, CallTranscript transcript)
        => gate.Writes = WriteAfterAsync(gate.Writes, gate, write, transcript.CallId, transcript.TurnIndex);

    /// <summary>Writes to the store, and lets the call outlive a store that refuses.</summary>
    private async Task WriteAfterAsync(
        Task previous, CallGate gate, Func<ValueTask> write, string callId, int turnIndex)
    {
        await previous.ConfigureAwait(false);

        try
        {
            await write().ConfigureAwait(false);
        }
#pragma warning disable CA1031 // A store 1 write failure never ends a call, and never breaks the chain behind it.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            Log.TranscriptWriteFailed(_logger, callId, turnIndex, exception);

            // The words are lost and the call is not. The report is what turns that into a fact the
            // host can count; the contract of TranscriptWriteDropped is that it cannot throw, which
            // is what keeps this chain from ever faulting.
            gate.Dropped?.Invoke(turnIndex, exception);
        }
    }

    /// <summary>Runs one piece of work against the call's transcript, alone.</summary>
    private TResult UnderLock<TResult>(AgentSession session, Func<CallTranscript, CallGate, TResult> work)
    {
        var gate = GateFor(session);

        lock (gate.Sync)
        {
            var transcript = _state.GetOrInitializeState(session);
            var result = work(transcript, gate);
            _state.SaveState(session, transcript);
            return result;
        }
    }

    private TResult UnderLock<TResult>(AgentSession session, Func<CallTranscript, TResult> work)
        => UnderLock(session, (transcript, _) => work(transcript));

    private CallGate GateFor(AgentSession session) => _gates.GetValue(session, static _ => new CallGate());

    /// <summary>What one call holds outside its state bag: its lock, and its queue of store writes.</summary>
    private sealed class CallGate
    {
        /// <summary>Gets the lock every read and every change of this call's transcript takes.</summary>
        public Lock Sync { get; } = new();

        /// <summary>Gets or sets the tail of this call's store writes. It never faults.</summary>
        public Task Writes { get; set; } = Task.CompletedTask;

        /// <summary>Gets or sets where a dropped write of this call is reported, if anywhere.</summary>
        public TranscriptWriteDropped? Dropped { get; set; }
    }
}
