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
/// <b>One gate per call covers the ordinal, the reply pointer, and the write.</b> A barge-in arrives
/// from the read loop, on another thread, while a run may still be finishing. Without the gate two
/// appends collide on one ordinal and the message is lost, or a truncate lands before the append it
/// targets and rewrites the previous turn's reply — a sentence the caller fully heard. The gate is
/// keyed weakly by session rather than held per call in a field, so it holds nothing when the call
/// ends and no two sessions share one.
/// </para>
/// </remarks>
internal sealed class AgentCoreChatHistoryProvider : ChatHistoryProvider
{
    private readonly ConditionalWeakTable<AgentSession, SemaphoreSlim> _gates = [];
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
    /// <param name="cancellationToken">Cancels the wait for the call's gate.</param>
    /// <remarks>
    /// The framework knows what a session is and has never heard of a turn, so the turn loop says
    /// so here before each run. It also closes the previous turn's reply to a cut — see
    /// <see cref="CallTranscript.BeginTurn"/>.
    /// </remarks>
    public ValueTask BeginTurnAsync(
        AgentSession session, string callId, int turnIndex, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrEmpty(callId);

        return UnderGateAsync(
            session,
            transcript =>
            {
                transcript.CallId = callId;
                transcript.BeginTurn(turnIndex);
                return default;
            },
            cancellationToken);
    }

    /// <summary>Replaces this turn's reply with the words the caller actually heard.</summary>
    /// <param name="session">The session of the call.</param>
    /// <param name="heard">The text the caller heard, as the vendor reported it. Nothing is estimated.</param>
    /// <param name="played">How much of the reply was played before the caller cut in.</param>
    /// <param name="cancellationToken">Cancels the wait for the call's gate.</param>
    /// <returns>
    /// <see langword="true"/> when a reply was cut, and <see langword="false"/> when this turn had
    /// none to cut. A caller that gets <see langword="false"/> still owes the record the heard text,
    /// and must hand it to the append rather than assume the cut landed.
    /// </returns>
    /// <remarks>
    /// It belongs to no run: a barge-in arrives from the read loop, on another thread. It never
    /// guesses which reply was meant.
    /// </remarks>
    public ValueTask<bool> TruncateLastReplyAsync(
        AgentSession session,
        string heard,
        TimeSpan played,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(heard);

        return UnderGateAsync(
            session,
            async transcript =>
            {
                if (transcript.TruncateLastReply(heard) is not { } row)
                {
                    return false;
                }

                Log.ReplyTruncated(_logger, row.CallId, row.TurnIndex, played.TotalMilliseconds);

                await WriteAsync(
                    () => _store.RewriteAsync(row.CallId, row.Ordinal, row.Content, cancellationToken),
                    row.CallId,
                    row.TurnIndex).ConfigureAwait(false);

                return true;
            },
            cancellationToken);
    }

    /// <summary>Writes the caller-facing turn of a graph row.</summary>
    /// <param name="session">The session of the call.</param>
    /// <param name="spoken">What the caller said.</param>
    /// <param name="heard">What the caller heard: the final node's reply, and no other node's.</param>
    /// <param name="cancellationToken">Cancels the wait for the call's gate.</param>
    /// <remarks>
    /// A workflow accepts no chat history provider, so the framework cannot drive store 1 for a
    /// graph and the turn loop drives it here instead. Store 1 holds the caller-facing turn only —
    /// the graph's node-to-node chatter never enters it.
    /// </remarks>
    public ValueTask AppendCallerFacingTurnAsync(
        AgentSession session,
        ChatMessage spoken,
        ChatMessage heard,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(spoken);
        ArgumentNullException.ThrowIfNull(heard);

        return AppendAsync(session, [spoken, heard], cancellationToken);
    }

    /// <inheritdoc />
    protected override async ValueTask<IEnumerable<ChatMessage>> ProvideChatHistoryAsync(
        InvokingContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.Session is not { } session
            ? []
            : await UnderGateAsync(session, transcript => ValueTask.FromResult(transcript.Read()), cancellationToken)
                .ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The base hands over only the new messages — its request filter already drops everything this
    /// provider supplied — so this is a straight append with nothing rebuilt.
    /// </remarks>
    protected override ValueTask StoreChatHistoryAsync(
        InvokedContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Session is not { } session)
        {
            return default;
        }

        ChatMessage[] written = [.. context.RequestMessages, .. context.ResponseMessages ?? []];
        return written.Length == 0 ? default : AppendAsync(session, written, cancellationToken);
    }

    private ValueTask AppendAsync(
        AgentSession session, ChatMessage[] messages, CancellationToken cancellationToken)
        => UnderGateAsync(
            session,
            async transcript =>
            {
                var rows = transcript.Append(messages);
                await WriteAsync(
                    () => _store.AppendAsync(rows, cancellationToken),
                    transcript.CallId,
                    transcript.TurnIndex).ConfigureAwait(false);
            },
            cancellationToken);

    /// <summary>Runs one piece of work against the call's transcript, alone.</summary>
    private async ValueTask<TResult> UnderGateAsync<TResult>(
        AgentSession session,
        Func<CallTranscript, ValueTask<TResult>> work,
        CancellationToken cancellationToken)
    {
        var gate = GateFor(session);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var transcript = TranscriptOf(session);
            var result = await work(transcript).ConfigureAwait(false);
            Save(session, transcript);
            return result;
        }
        finally
        {
            _ = gate.Release();
        }
    }

    private async ValueTask UnderGateAsync(
        AgentSession session,
        Func<CallTranscript, ValueTask> work,
        CancellationToken cancellationToken)
        => _ = await UnderGateAsync(
            session,
            async transcript =>
            {
                await work(transcript).ConfigureAwait(false);
                return true;
            },
            cancellationToken).ConfigureAwait(false);

    /// <summary>Writes to the store, and lets the call outlive a store that refuses.</summary>
    private async ValueTask WriteAsync(Func<ValueTask> write, string callId, int turnIndex)
    {
        try
        {
            await write().ConfigureAwait(false);
        }
#pragma warning disable CA1031 // A store 1 write failure never ends a call.
        catch (Exception exception) when (exception is not OperationCanceledException)
#pragma warning restore CA1031
        {
            Log.TranscriptWriteFailed(_logger, callId, turnIndex, exception);
        }
    }

    private SemaphoreSlim GateFor(AgentSession session)
        => _gates.GetValue(session, static _ => new SemaphoreSlim(1, 1));

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
}
