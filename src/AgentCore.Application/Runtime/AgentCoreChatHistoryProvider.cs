using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentCore.Application.Diagnostics;
using AgentCore.Application.Ports;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentCore.Application.Runtime;

/// <summary>
/// Store 1: the words of a call, held by the session and written through to a backing store.
/// </summary>
/// <remarks>
/// <para>
/// It is the adapter between the framework's invocation contexts and <see cref="CallTranscript"/>,
/// which holds the rules. What lives here is what the transcript cannot hold: the gate, the writes,
/// and the policy for a write that fails.
/// </para>
/// <para>
/// One instance is compiled once and shared by every call, so nothing about a call lives in a field
/// here. Per-call state lives in the session's <see cref="AgentSession.StateBag"/>, under this
/// provider's own <see cref="ChatHistoryProvider.StateKeys"/> entry.
/// </para>
/// <para>
/// <b>The reads are the framework's and the writes are <see cref="CallSession"/>'s.</b> The
/// framework calls <see cref="ProvideChatHistoryAsync"/> before every run, which is what lets a turn
/// pass the new caller message alone. It also offers to store the finished run, and this provider
/// refuses that offer. <see cref="StoreChatHistoryAsync"/> holds the two measurements behind that
/// refusal, and is the only place they are written down.
/// </para>
/// <para>
/// <b>A write failure never ends a call.</b> Every write catches, logs, and returns. The live
/// history stays in the session, so a store that is down costs the durable record of the turn and
/// nothing else — the caller notices nothing and the next turn still has the whole conversation.
/// </para>
/// <para>
/// <b>The read is served from the session and never from the store.</b> Returning an empty history
/// on a failed read would run the turn with no memory of the call, which is worse than any dropped
/// row.
/// </para>
/// <para>
/// <b>Every member here is synchronous, and the store write is queued behind the call's own
/// chain.</b> A barge-in arrives on the relay's read loop through <see cref="CallSession.Interrupt"/>,
/// which is synchronous and must not block: a read loop that waits on a database round trip stops
/// receiving frames. So a call's transcript moves under a plain lock, in memory, with no I/O inside
/// it, and the row it produced is appended to that call's write chain. The chain is what keeps the
/// order: a rewrite is queued behind the insert it corrects, so it can never overtake it and find no
/// row to rewrite.
/// </para>
/// <para>
/// <b>The lock is what keeps two writers off one ordinal.</b> Without it two appends collide on one
/// ordinal and a message is lost, because a failed write is swallowed and nothing would report it.
/// The lock is keyed weakly by session rather than held per call in a field, so it holds nothing
/// when the call ends and no two sessions share one.
/// </para>
/// </remarks>
internal sealed class AgentCoreChatHistoryProvider : ChatHistoryProvider
{
    private readonly ConditionalWeakTable<AgentSession, CallGate> _gates = [];
    private readonly ICallMessageStore _store;
    private readonly ILogger _logger;

    /// <summary>Creates the provider over a backing store.</summary>
    /// <param name="store">Where the words are written, or <see langword="null"/> for memory.</param>
    /// <param name="logger">Where a dropped write is reported, or <see langword="null"/> for none.</param>
    public AgentCoreChatHistoryProvider(ICallMessageStore? store = null, ILogger? logger = null)
    {
        _store = store ?? new InMemoryCallMessageStore();
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Stamps the call and the turn the next run belongs to.</summary>
    /// <param name="session">The session of the call.</param>
    /// <param name="callId">The id of the call.</param>
    /// <param name="turnIndex">The zero-based index of the turn about to run.</param>
    /// <remarks>
    /// The framework knows what a session is and has never heard of a turn, so the turn loop says so
    /// here before each run.
    /// </remarks>
    public void BeginTurn(AgentSession session, string callId, int turnIndex)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrEmpty(callId);

        UnderLock(
            session,
            transcript =>
            {
                transcript.CallId = callId;
                transcript.BeginTurn(turnIndex);
                return true;
            });
    }

    /// <summary>Reads the whole call, oldest message first.</summary>
    /// <param name="session">The session of the call.</param>
    /// <returns>The live history. It is a copy, so a later turn cannot tear it.</returns>
    public IReadOnlyList<ChatMessage> Read(AgentSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        return UnderLock(session, static transcript => transcript.Read());
    }

    /// <summary>Adds one finished turn's messages to the call.</summary>
    /// <param name="session">The session of the call.</param>
    /// <param name="messages">What the turn added, oldest first.</param>
    /// <remarks>
    /// <see cref="CallSession"/> hands over the messages it shaped, and not the messages the run
    /// produced. The two differ on a turn the caller cut short, where the record holds the words the
    /// caller heard and the tool pairs that finished.
    /// </remarks>
    public void AppendTurn(AgentSession session, IReadOnlyList<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(messages);

        if (messages.Count == 0)
        {
            return;
        }

        UnderLock(
            session,
            (transcript, gate) =>
            {
                var rows = transcript.Append(messages);
                Enqueue(gate, () => _store.AppendAsync(rows, CancellationToken.None), transcript);
                return true;
            });
    }

    /// <summary>Replaces the reply the caller was hearing with the words the caller actually heard.</summary>
    /// <param name="session">The session of the call.</param>
    /// <param name="heard">The text the caller heard, as the vendor reported it. Nothing is estimated.</param>
    /// <param name="played">How much of the reply was played before the caller cut in.</param>
    /// <returns>
    /// <see langword="true"/> when a reply was cut, and <see langword="false"/> when the call has
    /// none to cut. A caller that gets <see langword="false"/> still owes the record the heard text,
    /// and must hand it to <see cref="AppendTurn"/> rather than assume the cut landed.
    /// </returns>
    /// <remarks>
    /// It belongs to no run: a barge-in arrives from the read loop, on another thread. It cuts the
    /// last reply that carried words, whichever turn produced it, because
    /// <see cref="CallSession"/> is what decides that a barge-in belongs to the turn that finished
    /// last — and under a held prompt the next turn has already begun by then. A run that was cut
    /// before it spoke has appended nothing, so there is nothing here to find and the append records
    /// the already-cut text.
    /// </remarks>
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

    /// <summary>Waits for every write this call has queued to reach the store.</summary>
    /// <param name="session">The session of the call.</param>
    /// <returns>A task that completes when the chain is empty.</returns>
    /// <remarks>
    /// Nothing on the call path waits for a write. This is for the teardown that must not drop the
    /// session before its words are durable, and for a test that reads the store back.
    /// </remarks>
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
    /// <remarks>
    /// <para>
    /// <b>It deliberately stores nothing.</b> Two measurements against Microsoft.Agents.AI 1.17.0
    /// say the framework's own store hook cannot be store 1's writer, and
    /// <see cref="CallSession"/> writes every row instead — for all four compile rows alike, through
    /// <see cref="AppendTurn"/>.
    /// </para>
    /// <para>
    /// First, a run the caller cut short never reaches this hook at all: the framework reports the
    /// cancellation as a failure and stores nothing, so the turn the caller actually heard would be
    /// the one turn missing from the record.
    /// </para>
    /// <para>
    /// Second, this hook receives the request messages verbatim, and the message a turn sends
    /// carries the <c>&lt;system-reminder&gt;</c> of <see cref="State.UnfilledSlotReminder"/>. That
    /// reminder rides exactly one request by design; stored, it would repeat in every later prompt
    /// of the call.
    /// </para>
    /// </remarks>
    protected override ValueTask StoreChatHistoryAsync(
        InvokedContext context, CancellationToken cancellationToken = default) => default;

    /// <summary>Queues one store write behind everything this call has already queued.</summary>
    private void Enqueue(CallGate gate, Func<ValueTask> write, CallTranscript transcript)
        => gate.Writes = WriteAfterAsync(gate.Writes, write, transcript.CallId, transcript.TurnIndex);

    /// <summary>Writes to the store, and lets the call outlive a store that refuses.</summary>
    private async Task WriteAfterAsync(Task previous, Func<ValueTask> write, string callId, int turnIndex)
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
        }
    }

    /// <summary>Runs one piece of work against the call's transcript, alone.</summary>
    private TResult UnderLock<TResult>(AgentSession session, Func<CallTranscript, CallGate, TResult> work)
    {
        var gate = GateFor(session);

        lock (gate.Sync)
        {
            var transcript = TranscriptOf(session);
            var result = work(transcript, gate);
            Save(session, transcript);
            return result;
        }
    }

    private TResult UnderLock<TResult>(AgentSession session, Func<CallTranscript, TResult> work)
        => UnderLock(session, (transcript, _) => work(transcript));

    private CallGate GateFor(AgentSession session) => _gates.GetValue(session, static _ => new CallGate());

    private CallTranscript TranscriptOf(AgentSession session)
    {
        if (session.StateBag.TryGetValue<CallTranscript>(StateKeys[0], out var transcript, StateOptions)
            && transcript is not null)
        {
            return transcript;
        }

        transcript = new CallTranscript();

        Save(session, transcript);

        return transcript;
    }

    private void Save(AgentSession session, CallTranscript transcript)
        => session.StateBag.SetValue(StateKeys[0], transcript, StateOptions);

    /// <summary>Serialises a stored <see cref="ChatMessage"/> with the converters the framework ships.</summary>
    private static JsonSerializerOptions StateOptions => AIJsonUtilities.DefaultOptions;

    /// <summary>What one call holds outside its state bag: its lock, and its queue of store writes.</summary>
    private sealed class CallGate
    {
        /// <summary>Gets the lock every read and every change of this call's transcript takes.</summary>
        public Lock Sync { get; } = new();

        /// <summary>Gets or sets the tail of this call's store writes. It never faults.</summary>
        public Task Writes { get; set; } = Task.CompletedTask;
    }
}
