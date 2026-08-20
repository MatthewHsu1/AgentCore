using AgentCore.Application.Diagnostics;
using AgentCore.Application.Ports;
using AgentCore.Application.Transcript.Memory;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace AgentCore.Application.Transcript;

/// <summary>
/// Reports one store 1 write that was dropped, so the call can raise a diagnostic for it.
/// </summary>
/// <param name="turnIndex">The turn whose words were lost.</param>
/// <param name="exception">Why the store refused the write.</param>
internal delegate void TranscriptWriteDropped(int turnIndex, Exception exception);

/// <summary>
/// Store 1: the words of a call, held by the session and written through to a backing store.
/// </summary>
internal sealed class AgentCoreChatHistoryProvider : ChatHistoryProvider
{
    private readonly ConditionalWeakTable<AgentSession, CallGate> _gates = [];
    private readonly ITranscriptStore _store;
    private readonly ILogger _logger;

    /// <summary>Creates the provider over a backing store.</summary>
    /// <param name="store">Where the words are written, or <see langword="null"/> for memory.</param>
    /// <param name="logger">Where a dropped write is reported, or <see langword="null"/> for none.</param>
    public AgentCoreChatHistoryProvider(ITranscriptStore? store = null, ILogger? logger = null)
    {
        _store = store ?? new InMemoryTranscriptStore();
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Opens a call on one session: names it, and says where a dropped write is reported.
    /// </summary>
    public void BeginCall(AgentSession session, string callId, TranscriptWriteDropped? report = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrEmpty(callId);

        UnderLock(
            session,
            (transcript, gate) =>
            {
                transcript.CallId = callId;
                gate.Dropped = report;
                return true;
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

    /// <summary>
    /// Adds one finished turn's messages to the call.
    /// </summary>
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

    /// <summary>
    /// Adds the caller-facing turn of a graph row: what the caller said, and what it heard.
    /// </summary>
    public void AppendCallerFacingTurn(AgentSession session, ChatMessage spoken, ChatMessage? heard)
    {
        ArgumentNullException.ThrowIfNull(spoken);

        AppendTurn(session, heard is null ? [spoken] : [spoken, heard]);
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

        /// <summary>Gets or sets where a dropped write of this call is reported, if anywhere.</summary>
        public TranscriptWriteDropped? Dropped { get; set; }
    }
}
