using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Speech;
using AgentCore.AspNetCore.Sessions;
using AgentCore.AspNetCore.Speech;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentCore.AspNetCore.Vendors.TelnyxRelay;

/// <summary>
/// One socket, one call.
/// </summary>
/// <remarks>
/// <para>
/// Three tasks run for the life of the socket. The read loop parses frames and never waits for a
/// turn, because a turn that blocked the loop would make the <c>interrupt</c> frame unreachable
/// and barge-in impossible. The write loop is the only caller of
/// <see cref="WebSocket.SendAsync(ReadOnlyMemory{byte}, WebSocketMessageType, bool, CancellationToken)"/>,
/// because a WebSocket supports exactly one send at a time. The turn task streams the reply into
/// the write channel.
/// </para>
/// <para>
/// All three read one connection-scoped token, cancelled once for any reason the socket is going
/// away: the request aborted, the host is stopping, or one of the three tasks itself ended.
/// Nothing here waits on <c>HttpContext.RequestAborted</c> once teardown starts, because ASP.NET
/// Core recycles the context once the response completes and that token can then throw or name a
/// later request.
/// </para>
/// <para>
/// The adapter owns the vendor schema and the core does not, so D8 holds: this class translates
/// frames onto <see cref="IConversationPort"/> and nothing else.
/// </para>
/// <para>
/// One object fills both halves of the call's <see cref="SpeechChannel"/>, because one socket
/// carries both directions for this vendor. Nothing above the ports can tell, and nothing may ask.
/// The speaking side is <see cref="ISpeechOutputPort"/> below: every reply reaches the write loop
/// through <see cref="SpeakAsync"/>. The last of the two barge-in gates is this class's business
/// and nobody else's — <see cref="SpeechTurnArbiter"/> speaks in turn ids and never hands one over,
/// so this class keeps a monotonic speech generation instead, stamps every item it queues with the
/// generation it was queued under, and raises that generation in <see cref="StopAsync"/>. The write
/// loop then drops any item at or below the raised one, which is the same comparison the turn ids
/// used to answer, asked in the transport's own vocabulary.
/// </para>
/// <para>
/// One narrowing comes with that seam, and it is written down rather than hidden: a fragment the
/// arbiter had already decided to speak, but which only reaches <see cref="SpeakAsync"/> after
/// <see cref="StopAsync"/> raised the generation, is stamped with the new one and reaches the
/// caller. A port carries no reply identity to tell that fragment from the first fragment of the
/// next reply, and the window is the few instructions between the arbiter's own check and that call.
/// </para>
/// </remarks>
internal sealed class TelnyxRelayConnection : ISpeechInputPort, ISpeechOutputPort
{
    private readonly HttpContext _http;
    private readonly TelnyxRelayOptions _options;
    private readonly ILogger _logger;
    private readonly Channel<OutboundItem> _outbound;
    private readonly CancellationTokenSource _cancellation;

    // The same token every loop reads, captured once. Read off the field and never off
    // _cancellation.Token by anything that can outlive teardown: RunAsync disposes that source on
    // its way out, and a disposed source's Token getter throws, while the struct captured here goes
    // on answering "already cancelled" for as long as anyone holds it.
    private readonly CancellationToken _connectionToken;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ConnectionTaskObserver _observer;
    private readonly JsonWebSocketPump _pump;

    // The last of the two barge-in gates, in this connection's own vocabulary. SpeakAsync stamps
    // every item it queues with this counter plus one and StopAsync raises the counter itself when a
    // barge-in stops the reply; the write loop then drops anything at or below it. It lives on the
    // connection, alongside the channel the write loop reads, because the write loop is the one that
    // reads it and a second setup frame must not reset what a barge-in already cut off.
    private long _speechGeneration;

    // Zero until something takes the inbound stream, and one afterwards, forever. ListenAsync
    // promises one consumer for the life of the port, and this is what a second call trips over.
    private int _listening;

    private volatile CallSession? _session;

    // volatile for the same reason _session is: the read loop assigns it when the setup frame
    // arrives, and teardown reads it from another task to find the last turn.
    private volatile SpeechTurnArbiter? _arbiter;
    private bool _loggedPromptBeforeSetup;
    private bool _loggedMalformedInterrupt;
    private bool _loggedSecondSetup;

    private TelnyxRelayConnection(HttpContext http, WebSocket socket, TelnyxRelayOptions options, ILogger logger)
    {
        _http = http;
        _options = options;
        _logger = logger;

        // A dropped socket ends this call, and so does the host shutting down. Both stop the read
        // loop, the write loop, and any turn the same way, through the one token everything below
        // reads. IHostApplicationLifetime is resolved here, from the request's own provider, and
        // never at MapTelnyxRelay time: nothing is bound to it until a call actually arrives.
        _lifetime = http.RequestServices.GetRequiredService<IHostApplicationLifetime>();
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            http.RequestAborted,
            _lifetime.ApplicationStopping);
        _connectionToken = _cancellation.Token;

        // AddAgentCore registers this from options.TimeProvider, or TimeProvider.System when the
        // host bound none — the same clock CallSessionFactory already reads for callDurationSeconds.
        // Resolving it here, rather than reading TimeProvider.System directly, is what lets a test
        // own the idle deadline below by binding that one option.
        var timeProvider = http.RequestServices.GetRequiredService<TimeProvider>();

        // Bounded, so a slow relay slows the turn loop instead of growing a queue without a bound.
        _outbound = Channel.CreateBounded<OutboundItem>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });

        // The observer is vendor-free, so the two faults that are the relay's own doing reach it
        // through ClassifyTelnyxFault rather than through a catch clause of its own. The call id is
        // a function because the session only exists once the setup frame has arrived, which is
        // after this constructor and after some of the lines the observer logs.
        _observer = new ConnectionTaskObserver(
            () => _session?.CallId ?? "(before setup)",
            (callId, taskName) => TelnyxRelayLog.TeardownTimedOut(_logger, callId, taskName),
            (kind, callId, fault) => LogFault(kind, callId, fault),
            ClassifyTelnyxFault);

        // The pump owns the socket from here on: the read loop, the write loop, and the one close
        // this connection makes are all its, and none of the three names a vendor. The three seams
        // that would have are the delegates below — the exception the pump raises when the relay
        // breaks the contract, so DetermineCloseStatus still recognises it off the read loop's own
        // task, and the three log lines that name the relay by name. Each log callback closes over
        // _session for the same reason the observer's own callId does: the id only exists once the
        // setup frame has arrived, which is after this constructor.
        _pump = new JsonWebSocketPump(
            socket,
            new JsonWebSocketPumpOptions(
                _options.MaxFrameBytes,
                _options.IdleTimeout,
                _options.CloseTimeout,
                (status, message) => new RelayProtocolException(status, message),
                frameType => TelnyxRelayLog.UnknownFrameType(_logger, frameType, _session?.CallId ?? "(before setup)"),
                frameType => TelnyxRelayLog.FrameBodyRefused(_logger, frameType, _session?.CallId ?? "(before setup)"),
                () => TelnyxRelayLog.IdleTimeoutReached(_logger, _session?.CallId ?? "(before setup)")),
            timeProvider,
            _connectionToken);
    }

    /// <summary>Runs one call to its end.</summary>
    /// <param name="http">The request that carried the handshake.</param>
    /// <param name="socket">The accepted socket.</param>
    /// <param name="options">What the endpoint may do.</param>
    /// <returns>A task that completes when the socket is closed.</returns>
    public static Task RunAsync(HttpContext http, WebSocket socket, TelnyxRelayOptions options)
    {
        var logger = http.RequestServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("AgentCore.TelnyxRelay");

        return new TelnyxRelayConnection(http, socket, options, logger).RunAsync();
    }

    private async Task RunAsync()
    {
        // Both loops belong to the pump, and both seams into this class are named methods rather
        // than bodies: ParseFrame is the vendor's own reader, and DispatchAsync is what one parsed
        // frame means to this call. The cast is safe because ParseFrame is the only thing that ever
        // puts a frame into a FrameOutcome, and it only ever puts a RelayFrame there.
        var reading = _pump.ReadLoopAsync(ParseFrame, frame => DispatchAsync((RelayFrame)frame));
        var writing = _pump.WriteLoopAsync(_outbound.Reader, EncodeOutbound);

        try
        {
            await Task.WhenAny(reading, writing).ConfigureAwait(false);
        }
        finally
        {
            // The disposal below must run on every exit from here down, including a throw out of
            // Cancel() itself or store.RemoveAsync: _cancellation links to ApplicationStopping,
            // which lives for the whole process, and only Dispose() releases that registration.
            try
            {
                // One of the two loops just ended, for whatever reason, so the socket is going
                // away. Cancelling here stops the other loop's wait and any turn still streaming,
                // rather than leaving them to read a recycled HttpContext.RequestAborted after this
                // method returns. Cancel() itself is guarded: it runs with throwOnFirstException:
                // false, so every callback still runs and both loops are already cancelled before a
                // callback fault can reach this catch, and losing the rest of teardown below would
                // otherwise strand this call in the store for the life of the process.
                try
                {
                    _cancellation.Cancel();
                }
                catch (Exception fault)
                {
                    ConnectionTaskObserver.SafeLog(() => TelnyxRelayLog.CancellationFaulted(
                        _logger,
                        _session?.CallId ?? "(before setup)",
                        fault));
                }

                // The read loop must actually finish — and any session it was still creating must be
                // visible on this task — before _session is read for removal below. Awaiting it here,
                // rather than trusting Task.WhenAny already returned, is also what surfaces a fault
                // that raced the write loop instead of leaving it unobserved.
                await _observer.ObserveAsync(reading, ConnectionTaskKind.ReadLoop).ConfigureAwait(false);

                // The turn must stop writing before the channel is completed under it. Completing the
                // writer first would let a write that was already in flight throw ChannelClosedException
                // into a task nothing observes. Bounded: cancellation reaches a well-behaved turn, but
                // a turn that honours cancellation yet takes longer than CloseTimeout to unwind — or a
                // chat client that ignores its token altogether, such as BlockingChatClient in the test
                // fakes — must not be able to wedge teardown forever.
                //
                // The last turn is read through SpeechTurnArbiter.CurrentTurn, which takes the same
                // lock every writer of it takes, and never off a field directly: the arbiter's own
                // RunPendingPrompt can reassign it from a turn's own task, off the read loop, so
                // teardown here is no longer the only writer's own reader. That property's remarks
                // carry the rest of the reasoning, including what stops the reverse case — a fresh
                // turn appearing after teardown already read it. Null before the first setup frame,
                // because the arbiter is built with the session.
                var lastTurn = _arbiter?.CurrentTurn ?? Task.CompletedTask;

                await _observer.ObserveAsync(lastTurn, ConnectionTaskKind.Turn, _options.CloseTimeout).ConfigureAwait(false);

                _outbound.Writer.TryComplete();

                // Both reading and lastTurn are fully observed by this point — ObserveAsync already
                // awaited each of them inside its own try, which is what makes it safe to read
                // .Exception here without raising a fresh UnobservedTaskException. Determined now,
                // after teardown already knows how the read loop and the last turn each ended, and
                // before the one CloseOutputAsync call the pump makes, because a status decided
                // any later could not still change what the vendor already received. writing is not
                // yet observed at this point — it is only awaited after the close below — so passing
                // it in only lets DetermineCloseStatus see a fault it already has, never one that
                // has not happened yet; see the remarks on that method for what this can and cannot
                // catch.
                var (status, description) = DetermineCloseStatus(reading, lastTurn, writing);

                // No bounded wait for the write loop here: _cancellation already cancelled above, and
                // the write loop reads the channel with that same token, so it is already unblocking
                // on its own. CloseAsync is what actually ends a send stuck on relay backpressure — it
                // aborts the socket on its own timeout — so the write loop is observed after it, not
                // waited for before it. A throw out of CloseAsync itself must not skip that
                // observation or the store removal below, so it is caught and logged here instead of
                // left to propagate.
                try
                {
                    await _pump.CloseAsync(status, description).ConfigureAwait(false);
                }
                catch (Exception fault)
                {
                    ConnectionTaskObserver.SafeLog(() => TelnyxRelayLog.CloseFaulted(
                        _logger,
                        _session?.CallId ?? "(before setup)",
                        fault));
                }

                await _observer.ObserveAsync(writing, ConnectionTaskKind.WriteLoop).ConfigureAwait(false);

                if (_session is { } session)
                {
                    var store = _http.RequestServices.GetRequiredService<ICallSessionStore>();
                    await store.RemoveAsync(session.CallId, CancellationToken.None).ConfigureAwait(false);
                }
            }
            finally
            {
                _cancellation.Dispose();
            }
        }
    }

    /// <summary>Works out the status and the description the vendor sees on the close frame.</summary>
    /// <param name="reading">
    /// The read loop's task, already fully observed by <see cref="ConnectionTaskObserver.ObserveAsync"/>.
    /// </param>
    /// <param name="lastTurn">
    /// The last turn's task, already fully observed by <see cref="ConnectionTaskObserver.ObserveAsync"/>.
    /// </param>
    /// <param name="writing">
    /// The write loop's task. Unlike <paramref name="reading"/> and <paramref name="lastTurn"/>, this
    /// one is not yet observed — <c>RunAsync</c> only awaits it after the close this method's result
    /// feeds. Its <see cref="Task.IsFaulted"/> is still safe to read without observing it first:
    /// that flag reflects the task's current state the moment it is read, and reading it does not
    /// itself count as observing the exception for the unobserved-task-exception machinery the way
    /// awaiting or reading <see cref="Task.Exception"/> would.
    /// </param>
    /// <returns>The status to close with, and the description to send alongside it.</returns>
    /// <remarks>
    /// <para>
    /// Both <paramref name="reading"/> and <paramref name="lastTurn"/> are already completed and
    /// already observed by the time this runs — ObserveAsync awaited each of them inside its own
    /// try — so reading their <see cref="Task.Exception"/> here raises nothing new; it only reads
    /// what already happened.
    /// </para>
    /// <para>
    /// The checks are ordered by how specific each cause is. A <see cref="RelayProtocolException"/>
    /// names exactly why the endpoint refused the call, so it wins over everything else. A turn or
    /// the write loop faulting with anything other than its own cancellation is this connection's
    /// own defect, not the caller's or the vendor's, so either counts before the host-stopping check
    /// below even notices that <see cref="_cancellation"/> is, by this point in teardown, always
    /// already cancelled for every possible reason at once. Host-stopping is checked directly
    /// against <see cref="_lifetime"/> rather than against which task happened to notice the
    /// cancellation first, because once teardown's own call to <c>_cancellation.Cancel()</c> has
    /// run, every task is cancelled regardless of why teardown started, and none of their own
    /// completions can still tell "the host is stopping" apart from "the other loop just ended, so
    /// this cancelled too." A dropped socket with no close frame —
    /// <see cref="WebSocketError.ConnectionClosedPrematurely"/> — and a read loop that simply
    /// returned once it saw the relay's own close frame both fall through to the same default: an
    /// ordinary end of call.
    /// </para>
    /// <para>
    /// The <paramref name="writing"/> check only catches a write loop that has already faulted at
    /// the moment this method runs. It is not exhaustive: the write loop is still running at this
    /// point in the common case, and a fault it raises after this line — right up through the
    /// <c>SendAsync</c> the close itself makes — cannot change a status already chosen and already
    /// on its way to the vendor. That gap is an accepted limit of observing <paramref name="writing"/>
    /// before the close rather than after it, which the teardown order this method must not disturb
    /// already requires.
    /// </para>
    /// </remarks>
    private (WebSocketCloseStatus Status, string? Description) DetermineCloseStatus(
        Task reading,
        Task lastTurn,
        Task writing)
    {
        if (reading.IsFaulted && reading.Exception!.GetBaseException() is RelayProtocolException protocol)
        {
            return (protocol.Status, protocol.Message);
        }

        if (lastTurn.IsFaulted || writing.IsFaulted)
        {
            return (WebSocketCloseStatus.InternalServerError, null);
        }

        if (_lifetime.ApplicationStopping.IsCancellationRequested)
        {
            return (WebSocketCloseStatus.EndpointUnavailable, null);
        }

        if (reading.IsFaulted
            && reading.Exception!.GetBaseException() is not WebSocketException
            {
                WebSocketErrorCode: WebSocketError.ConnectionClosedPrematurely,
            })
        {
            return (WebSocketCloseStatus.InternalServerError, null);
        }

        return (WebSocketCloseStatus.NormalClosure, null);
    }

    /// <summary>Logs the two faults that are the vendor's doing rather than this endpoint's defect.</summary>
    /// <param name="fault">The exception a loop or a turn ended with.</param>
    /// <param name="kind">Which task faulted.</param>
    /// <param name="callId">The id of the call, or a placeholder before setup.</param>
    /// <returns>
    /// <see langword="true"/> when this method logged <paramref name="fault"/> itself, and
    /// <see langword="false"/> to leave it to <see cref="LogFault"/>.
    /// </returns>
    /// <remarks>
    /// The classify hook <see cref="ConnectionTaskObserver"/> calls before its own general clause.
    /// Both causes below name a vendor type, which is why they stay here rather than moving into
    /// that observer with the rest of the teardown machinery.
    /// </remarks>
    private bool ClassifyTelnyxFault(Exception fault, ConnectionTaskKind kind, string callId)
    {
        switch (fault)
        {
            case RelayProtocolException protocol:
                // Malformed JSON or an oversized frame: the relay broke the contract, not a defect
                // this endpoint owns. DetermineCloseStatus reads this same exception straight off
                // the task afterward for the close status, so this line only has to log it — every
                // other malformed-input line in this file (PromptBeforeSetup, FrameRefused,
                // MalformedInterruptFrame) already logs at Warning, and LogFault's generic path
                // would otherwise report this at Error, the same level as an unhandled bug.
                TelnyxRelayLog.RelayProtocolViolation(_logger, callId, protocol.Message);
                return true;

            case WebSocketException { WebSocketErrorCode: WebSocketError.ConnectionClosedPrematurely }
                when kind == ConnectionTaskKind.ReadLoop:
                // The call dropped with no close frame. Section "close statuses" of task 7: that is
                // an ordinary end of a call, not a fault, so it is worth a line but not an error one.
                TelnyxRelayLog.CallDroppedWithNoCloseFrame(_logger, callId);
                return true;

            default:
                return false;
        }
    }

    /// <summary>Logs the fault of a read loop, a turn, or a write loop, whichever <paramref name="kind"/> names.</summary>
    /// <param name="kind">Which task faulted.</param>
    /// <param name="callId">The id of the call, or a placeholder before setup.</param>
    /// <param name="fault">The cause.</param>
    /// <remarks>
    /// The <c>logFault</c> hook of <see cref="ConnectionTaskObserver"/>, which calls it from
    /// <see cref="ConnectionTaskObserver.ObserveAsync"/> directly and from the fault-only
    /// continuation that method attaches on a timeout. That continuation runs with nothing above it
    /// to catch a throw, so the observer wraps every call to this method in
    /// <see cref="ConnectionTaskObserver.SafeLog"/>; nothing here has to guard itself again.
    /// </remarks>
    private void LogFault(ConnectionTaskKind kind, string callId, Exception fault)
    {
        switch (kind)
        {
            case ConnectionTaskKind.Turn:
                TelnyxRelayLog.TurnFaulted(_logger, callId, fault);
                break;

            case ConnectionTaskKind.WriteLoop:
                TelnyxRelayLog.WriteLoopFaulted(_logger, callId, fault);
                break;

            case ConnectionTaskKind.ReadLoop:
            default:
                TelnyxRelayLog.ReadLoopFaulted(_logger, callId, fault);
                break;
        }
    }

    /// <summary>Reads one inbound message with the vendor's own reader.</summary>
    /// <param name="utf8">The whole message, already reassembled by the pump.</param>
    /// <returns>The frame, the name of a type this build does not model, or the name of one whose body would not bind.</returns>
    /// <remarks>
    /// The <c>parse</c> seam of <see cref="JsonWebSocketPump"/>. <see cref="FrameOutcome"/> says the
    /// same three things <see cref="TelnyxRelayFrameReader.TryRead"/> does, so the <see langword="bool"/>
    /// carries nothing the outcome does not: a frame is present exactly when that method returned
    /// <see langword="true"/>.
    /// </remarks>
    private static FrameOutcome ParseFrame(ReadOnlySpan<byte> utf8)
    {
        _ = TelnyxRelayFrameReader.TryRead(utf8, out var frame, out var unknownType, out var refusedType);
        return new FrameOutcome(frame, unknownType, refusedType);
    }

    /// <summary>Writes one queued item as the frame to send, or drops it.</summary>
    /// <param name="item">What a reply queued, and the speech generation it was queued under.</param>
    /// <param name="writer">The write loop's own writer, already reset for this frame.</param>
    /// <returns>
    /// <see langword="true"/> when a whole frame was written, and <see langword="false"/> to drop
    /// <paramref name="item"/> without sending anything.
    /// </returns>
    /// <remarks>
    /// The <c>encode</c> seam of <see cref="JsonWebSocketPump"/>, which runs it immediately before
    /// its one send. The gate below stays here, on this side of that seam, because a stale item is
    /// stale in this connection's own vocabulary and in no transport's. The writer belongs to that
    /// loop and is reused for every frame of the call, so nothing here keeps it, flushes it, or
    /// holds what it wrote: the bytes are the loop's to send and the loop's to overwrite.
    /// </remarks>
    private bool EncodeOutbound(OutboundItem item, Utf8JsonWriter writer)
    {
        // The write loop is the last gate, immediately before the one SendAsync call the pump
        // makes for this connection. SpeechTurnArbiter's own check only narrows the race — it cannot
        // stop a token already past it at the moment a barge-in raises the speech generation:
        // one already queued, one a bounded channel's backpressure was about to release into the gap
        // StopAsync's own drain just made, or one this loop had already dequeued and was about to
        // send. All three carry the generation they were queued under, the same counter that drain
        // raises, so whichever of them let a stale item this far, it stops here instead of reaching
        // the caller. The gate is expressed in generations and never in the arbiter's turn ids: an
        // item from a turn that simply ended, with a newer one already under way, is not stale, and
        // a turn id alone cannot tell the two apart — a generation only ever moves when a barge-in
        // actually stopped the audio.
        //
        // >=, not ==. _speechGeneration and every item's own Generation both only ever increase,
        // so "the highest generation a barge-in has cut off is at or above this item's own" is
        // the question that actually matches a call across two barge-ins: with == alone, an item of
        // generation N already dequeued here, then gated after a second, later barge-in raises
        // the mark to N+1, would compare N+1 != N, pass, and reach the caller — exactly the
        // straggler this gate exists to stop. With no interrupt at all _speechGeneration stays
        // 0, and a queued item's generation is always at least 1, so nothing is dropped that
        // this comparison would not already drop with ==. The one behaviour change is a prior
        // turn's trailing last: true frame, still queued when a later barge-in lands: it now
        // drops instead of reaching the caller, which is exactly what StopAsync's own
        // unconditional drain already does to that same frame whenever it is still queued at the
        // moment of the interrupt rather than sent moments later.
        if (item.Generation is { } generation && Interlocked.Read(ref _speechGeneration) >= generation)
        {
            return false;
        }

        // The same options SerializeToUtf8Bytes was given, and the same bytes: TelnyxRelayJson sets
        // no encoder and no indentation, so a default Utf8JsonWriter escapes and lays out this frame
        // exactly as the writer JsonSerializer used to build per call did.
        JsonSerializer.Serialize(writer, item.Frame, item.Frame.GetType(), TelnyxRelayJson.Options);
        return true;
    }

    private async Task DispatchAsync(RelayFrame frame)
    {
        switch (frame)
        {
            case RelayFrame.Setup setup:
                await StartCallAsync(setup).ConfigureAwait(false);
                break;

            case RelayFrame.Prompt { Last: true } prompt:
                await StartTurnAsync(prompt.VoicePrompt).ConfigureAwait(false);
                break;

            case RelayFrame.Prompt:
                // An interim transcript. The turn starts on the final one.
                break;

            case RelayFrame.Interrupt interrupt:
                HandleInterrupt(interrupt);
                break;

            case RelayFrame.Dtmf:
                // Never the digit itself. A keypad carries card numbers, PINs, and dates of birth,
                // and the house rule in Log.cs is that no line carries what the caller said.
                TelnyxRelayLog.DtmfReceived(_logger);
                break;

            case RelayFrame.Error error:
                // The vendor refused a frame this endpoint sent. That is our defect.
                TelnyxRelayLog.FrameRefused(_logger, _session?.CallId ?? "(before setup)", error.Description);
                break;
        }
    }

    private async Task StartCallAsync(RelayFrame.Setup setup)
    {
        var factory = _http.RequestServices.GetRequiredService<ICallSessionFactory>();
        var store = _http.RequestServices.GetRequiredService<ICallSessionStore>();

        // One socket carries one call, and the vendor sends one setup frame. A second one replaces
        // the session rather than refusing the socket, because section 7.1 forbids dropping a call
        // over a frame the vendor got wrong, and because the vendor's latest word about the call is
        // the one to answer. The first session is removed here and not left behind: teardown only
        // ever removes the session this connection currently holds, and InMemoryCallSessionStore
        // documents that it evicts nothing on its own, so a session skipped here would live for the
        // rest of the process. CancellationToken.None, because the removal must still run while the
        // connection is tearing down.
        if (_session is { } replaced)
        {
            if (!_loggedSecondSetup)
            {
                _loggedSecondSetup = true;
                TelnyxRelayLog.SecondSetupFrame(_logger, replaced.CallId);
            }

            await store.RemoveAsync(replaced.CallId, CancellationToken.None).ConfigureAwait(false);
        }

        // callSessionId groups the legs of one logical call, so the id survives the warm transfer
        // that slice 2 adds. A leg id would break there.
        _session = factory.Create(setup.CallSessionId);
        await store.AddAsync(_session, _cancellation.Token).ConfigureAwait(false);

        // Built on the first setup frame and never again: a later one only tells the arbiter which
        // call to answer from now on. One socket runs one turn at a time, and that promise belongs
        // to this connection rather than to any one session of it — a second arbiter would bring
        // its own _turnActive, its own held prompt, its own ids, and its own log-once flag, so a
        // prompt arriving while the replaced session's turn still ran would find nothing in flight
        // and start a second reply into the same channel. Rebinding keeps every one of those
        // exactly where the old, connection-held state kept them.
        if (_arbiter is { } arbiter)
        {
            arbiter.Rebind(_session);
            return;
        }

        // Not in the constructor: the arbiter runs turns against a session, and no session exists
        // until this frame arrives. The output port handed over is this connection itself — one
        // socket is both halves of this vendor's channel — and the arbiter cannot tell that from a
        // port belonging to a separate synthesizer, which is the whole of D8.
        // The two log callbacks are this vendor's own lines, bound here: the arbiter decides when a
        // prompt is held and when a further one is dropped, and names no log event of its own.
        _arbiter = new SpeechTurnArbiter(
            _session,
            this,
            _observer,
            callId => TelnyxRelayLog.PromptHeld(_logger, callId),
            callId => TelnyxRelayLog.PendingPromptDropped(_logger, callId),
            _connectionToken);
    }

    private Task StartTurnAsync(string text)
    {
        if (_arbiter is not { } arbiter)
        {
            // Log once for the call, not once for the frame. Same rule as the unknown-frame line.
            if (!_loggedPromptBeforeSetup)
            {
                _loggedPromptBeforeSetup = true;
                TelnyxRelayLog.PromptBeforeSetup(_logger);
            }

            return Task.CompletedTask;
        }

        // Discarded, never awaited. The arbiter's own task follows the turn to its end, and the read
        // loop is what calls this: a read loop that waited for a turn could not receive the
        // interrupt frame that ends it. The turn itself stays visible on CurrentTurn, which the next
        // turn and teardown both observe.
        _ = arbiter.StartTurnAsync(text);
        return Task.CompletedTask;
    }

    private void HandleInterrupt(RelayFrame.Interrupt interrupt)
    {
        if (_arbiter is not { } arbiter || _session is not { } session)
        {
            return;
        }

        // Section 7.1's rule for an unknown frame type and for a prompt before setup applies here
        // too: a frame the vendor got wrong must not drop the call. System.Text.Json enforces
        // neither a missing property nor a negative number at run time, so a frame missing
        // utteranceUntilInterrupt, or carrying a negative durationUntilInterruptMs, would otherwise
        // reach CallSession.Interrupt's own ArgumentNullException or ArgumentOutOfRangeException
        // uncaught and take the read loop down with it. Nothing here clamps the duration — a clamp
        // is an estimate, and D28 exists to keep estimates out of this path. This guard stays here,
        // and never moves into the arbiter: validating these two values is reading a vendor frame.
        if (interrupt.UtteranceUntilInterrupt is null || interrupt.DurationUntilInterruptMs < 0)
        {
            if (!_loggedMalformedInterrupt)
            {
                _loggedMalformedInterrupt = true;
                TelnyxRelayLog.MalformedInterruptFrame(_logger, session.CallId);
            }

            return;
        }

        // The vendor measured both values itself. D28 and item 6a: nothing here estimates either
        // one.
        arbiter.Interrupt(
            interrupt.UtteranceUntilInterrupt,
            TimeSpan.FromMilliseconds(interrupt.DurationUntilInterruptMs));

        // Logged last, once the raised id, the call to Interrupt, and the drain behind
        // ISpeechOutputPort.StopAsync have all already happened, so this line is proof the whole
        // guard is in place — never the words the caller said or heard.
        TelnyxRelayLog.InterruptReceived(_logger, session.CallId);
    }

    /// <inheritdoc />
    public ValueTask SpeakAsync(string fragment, CancellationToken cancellationToken = default)
        => _outbound.Writer.WriteAsync(
            new OutboundItem(CurrentGeneration() + 1, new RelayToken(fragment, Last: false)),
            cancellationToken);

    /// <inheritdoc />
    public ValueTask CompleteAsync(CancellationToken cancellationToken = default)
        // The vendor closes a reply on last: true, and the sample uses an empty final token when
        // the stream ended with no trailing text.
        => _outbound.Writer.WriteAsync(
            new OutboundItem(CurrentGeneration() + 1, new RelayToken(string.Empty, Last: true)),
            cancellationToken);

    /// <inheritdoc />
    public ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        // Raised before the drain, and the write loop's own gate reads the same field. The relay
        // already stopped its own audio. Conversation Relay carries no clear frame, so anything
        // still queued here is the only audio left that could start it playing again, and this
        // queue is the only thing this process still controls.
        Interlocked.Increment(ref _speechGeneration);

        while (_outbound.Reader.TryRead(out _))
        {
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <b>This stream yields nothing today, and that is deliberate rather than a defect.</b> The
    /// read loop still drives <c>DispatchAsync</c> directly, so every inbound frame of this call
    /// already reaches the arbiter by that path; rerouting it through this stream is a later change
    /// that no current caller needs. Until then this half of the channel exists so the shape is
    /// honest — one object really does fill both slots — and it completes when the connection does,
    /// so an <c>await foreach</c> over it ends with the call rather than at once. A consumer that
    /// cancels its own read gets <see cref="OperationCanceledException"/> instead, which is the rule
    /// <see cref="ISpeechInputPort.ListenAsync"/> sets for every implementation of this port.
    /// </para>
    /// <para>
    /// One consumer for the life of the port, and the guard is here rather than inside the iterator
    /// below: an iterator's body does not run until something enumerates it, so a second call would
    /// otherwise hand back a stream that throws late, or never at all if nobody enumerates it.
    /// </para>
    /// </remarks>
    public IAsyncEnumerable<SpeechInput> ListenAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _listening, 1, 0) != 0)
        {
            throw new InvalidOperationException("This relay connection is already being read.");
        }

        return ListenCoreAsync(cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Nothing to release here, and disposing a port must not tear this connection down. The
    /// channel, the linked cancellation source, and the socket all belong to <c>RunAsync</c>, which
    /// releases them in its own teardown once the last turn has stopped writing — and for this
    /// vendor the call ends when that method returns, not when whoever holds the
    /// <see cref="SpeechChannel"/> lets go of it.
    /// </remarks>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>Yields nothing, and completes when this connection ends.</summary>
    /// <param name="cancellationToken">Ends the stream early, ahead of the connection itself.</param>
    /// <returns>An empty stream, deliberately. <see cref="ListenAsync"/>'s remarks say why.</returns>
    private async IAsyncEnumerable<SpeechInput> ListenCoreAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Linked, so the caller's own token ends the stream too, and built from the captured
        // connection token rather than from _cancellation.Token for the reason that field records.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_connectionToken, cancellationToken);

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Which token fired is the whole difference, and ISpeechInputPort.ListenAsync settles
            // it: the connection ending is the ordinary end of the call and completes this stream,
            // while the consumer's own cancellation throws, as any cancelled await does. Rethrown
            // through the caller's token rather than by letting the linked one out, so the exception
            // names the token the caller actually cancelled. Both fired at once counts as
            // cancellation: the consumer asked to stop, and it gets the answer it asked for.
            cancellationToken.ThrowIfCancellationRequested();
        }

        yield break;
    }

    /// <summary>Reads the newest speech generation a barge-in has cut off, or 0 when none has.</summary>
    /// <returns>The counter, read the same way the write loop's own gate reads it.</returns>
    private long CurrentGeneration() => Interlocked.Read(ref _speechGeneration);
}

/// <summary>The relay broke the contract, and the socket must close with a reason.</summary>
/// <param name="status">The close status the vendor should see.</param>
/// <param name="message">Why the endpoint refused.</param>
internal sealed class RelayProtocolException(WebSocketCloseStatus status, string message)
    : Exception(message)
{
    /// <summary>Gets the status the socket closes with.</summary>
    public WebSocketCloseStatus Status { get; } = status;
}

/// <summary>One item queued for the write loop, carrying the speech generation it was written under.</summary>
/// <param name="Generation">
/// The speech generation <see cref="Frame"/> was queued under, or <see langword="null"/> for a
/// frame no reply owns — nothing writes one of those yet, but the write loop's gate only applies
/// when a generation is present, so a future frame outside the reply lifecycle (a close handoff,
/// for one) can opt out by carrying none. This type never reaches <see cref="TelnyxRelayJson"/>:
/// only <see cref="Frame"/> is serialized, so nothing here changes the wire.
/// </param>
/// <param name="Frame">The frame to serialize and send: a <see cref="RelayToken"/> today.</param>
internal readonly record struct OutboundItem(long? Generation, object Frame);
