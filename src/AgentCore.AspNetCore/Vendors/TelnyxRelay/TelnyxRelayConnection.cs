using System.Buffers;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using AgentCore.Application.Runtime;
using AgentCore.AspNetCore.Sessions;
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
/// frames onto <see cref="Application.Ports.IConversationPort"/> and nothing else.
/// </para>
/// </remarks>
internal sealed class TelnyxRelayConnection
{
    private readonly HttpContext _http;
    private readonly WebSocket _socket;
    private readonly TelnyxRelayOptions _options;
    private readonly ILogger _logger;
    private readonly Channel<OutboundItem> _outbound;
    private readonly CancellationTokenSource _cancellation;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly TimeProvider _timeProvider;

    private volatile CallSession? _session;
    private Task _turn = Task.CompletedTask;
    private bool _loggedUnknownFrame;
    private bool _loggedRefusedFrameBody;
    private bool _loggedPromptBeforeSetup;
    private bool _loggedPendingPromptDropped;
    private bool _loggedMalformedInterrupt;
    private bool _loggedSecondSetup;

    // Guards _turnId, _interruptedTurnId, _turnActive, _pendingPrompt, and every place _turn is
    // read then reassigned. The read loop reaches this lock through StartTurnAsync and
    // HandleInterrupt, one frame at a time; a turn's own task reaches it too, from
    // RunPendingPrompt, once it finishes. _turnId and _interruptedTurnId are also read without the
    // lock, through Interlocked.Read, from inside a turn's own hot loop and from the write loop's
    // own gate, where taking a lock on every update or every outbound frame would slow the two
    // paths this guard exists to protect.
    private readonly Lock _turnLock = new();
    private long _turnId;

    // The id a barge-in invalidated, or 0 when nothing has been interrupted yet. This is a
    // separate field from _turnId, not a reuse of it, because _turnId also advances on every
    // ordinary turn transition — RunPendingPrompt mints the next id as soon as one turn's own task
    // finishes, which can race ahead of the write loop still sending that turn's own trailing
    // frames. A single shared "current id" field cannot tell "a newer turn has started normally"
    // apart from "this turn was cut short": both look like a mismatch to a check that only asks
    // "does this item's id equal the current one." Recording which id was interrupted, instead of
    // just raising a shared counter, is what lets a turn's own guard — and the write loop's —
    // ask the question that actually matters: was *this* turn, specifically, the one barge-in cut
    // off, regardless of how many ordinary turns have started since.
    private long _interruptedTurnId;

    // The newest turn whose output this connection has actually handed to the relay, or 0 while no
    // turn has produced a word. It is not _turnId, and the difference is the whole of the barge-in
    // target: Telnyx paces the audio itself, so turn N is still being spoken long after its own
    // stream ended, and RunPendingPrompt starts turn N+1 inside turn N's own finally. A barge-in in
    // that window belongs to turn N — the turn the caller was hearing — and marking N+1 instead
    // would drop every update of a turn nobody has heard yet and skip its closing last: true frame,
    // leaving the caller with dead air and the vendor with an unfinished reply.
    private long _spokenTurnId;
    private bool _turnActive;
    private string? _pendingPrompt;

    private TelnyxRelayConnection(HttpContext http, WebSocket socket, TelnyxRelayOptions options, ILogger logger)
    {
        _http = http;
        _socket = socket;
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

        // AddAgentCore registers this from options.TimeProvider, or TimeProvider.System when the
        // host bound none — the same clock CallSessionFactory already reads for callDurationSeconds.
        // Resolving it here, rather than reading TimeProvider.System directly, is what lets a test
        // own the idle deadline below by binding that one option.
        _timeProvider = http.RequestServices.GetRequiredService<TimeProvider>();

        // Bounded, so a slow relay slows the turn loop instead of growing a queue without a bound.
        _outbound = Channel.CreateBounded<OutboundItem>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
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
        var reading = ReadLoopAsync();
        var writing = WriteLoopAsync();

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
                    SafeLog(() => TelnyxRelayLog.CancellationFaulted(
                        _logger,
                        _session?.CallId ?? "(before setup)",
                        fault));
                }

                // The read loop must actually finish — and any session it was still creating must be
                // visible on this task — before _session is read for removal below. Awaiting it here,
                // rather than trusting Task.WhenAny already returned, is also what surfaces a fault
                // that raced the write loop instead of leaving it unobserved.
                await ObserveAsync(reading, ConnectionTask.ReadLoop).ConfigureAwait(false);

                // The turn must stop writing before the channel is completed under it. Completing the
                // writer first would let a write that was already in flight throw ChannelClosedException
                // into a task nothing observes. Bounded: cancellation reaches a well-behaved turn, but
                // a turn that honours cancellation yet takes longer than CloseTimeout to unwind — or a
                // chat client that ignores its token altogether, such as BlockingChatClient in the test
                // fakes — must not be able to wedge teardown forever.
                //
                // _turn is read under _turnLock, not off the field directly. RunPendingPrompt can
                // reassign _turn from a turn's own task, off the read loop, so teardown here is no
                // longer the only writer's own reader: without the lock, this could observe a stale
                // reference and let the real last turn finish unobserved. RunPendingPrompt itself
                // checks _cancellation before it will start a turn from a held prompt, which is what
                // stops the reverse case — a fresh turn appearing in _turn after teardown already
                // read it.
                Task lastTurn;
                lock (_turnLock)
                {
                    lastTurn = _turn;
                }

                await ObserveAsync(lastTurn, ConnectionTask.Turn, _options.CloseTimeout).ConfigureAwait(false);

                _outbound.Writer.TryComplete();

                // Both reading and lastTurn are fully observed by this point — ObserveAsync already
                // awaited each of them inside its own try, which is what makes it safe to read
                // .Exception here without raising a fresh UnobservedTaskException. Determined now,
                // after teardown already knows how the read loop and the last turn each ended, and
                // before the one CloseOutputAsync call this class makes, because a status decided
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
                    await CloseAsync(status, description).ConfigureAwait(false);
                }
                catch (Exception fault)
                {
                    SafeLog(() => TelnyxRelayLog.CloseFaulted(
                        _logger,
                        _session?.CallId ?? "(before setup)",
                        fault));
                }

                await ObserveAsync(writing, ConnectionTask.WriteLoop).ConfigureAwait(false);

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
    /// <param name="reading">The read loop's task, already fully observed by <see cref="ObserveAsync"/>.</param>
    /// <param name="lastTurn">The last turn's task, already fully observed by <see cref="ObserveAsync"/>.</param>
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

    /// <summary>Which task <see cref="ObserveAsync"/> is watching, so it logs the right line.</summary>
    private enum ConnectionTask
    {
        ReadLoop,
        Turn,
        WriteLoop,
    }

    /// <summary>Awaits a loop or a turn task, and never lets its fault go unobserved.</summary>
    /// <param name="task">The read loop's task, the write loop's task, or a turn's task.</param>
    /// <param name="kind">Which log line names the fault, if there is one.</param>
    /// <param name="timeout">
    /// How long to wait before giving up on <paramref name="task"/>, or <see langword="null"/> to
    /// wait for it unconditionally.
    /// </param>
    /// <remarks>
    /// A cancellation raised by <see cref="_cancellation"/> is teardown this connection asked for
    /// itself, so it stays quiet. Anything else is the silence a voice call must never answer with,
    /// per the house rule in <see cref="Application.Diagnostics.Log"/>.
    /// </remarks>
    private async Task ObserveAsync(Task task, ConnectionTask kind, TimeSpan? timeout = null)
    {
        try
        {
            if (timeout is { } bound)
            {
                await task.WaitAsync(bound).ConfigureAwait(false);
            }
            else
            {
                await task.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // This connection's own token just cancelled it. That is teardown, not a fault.
        }
        catch (TimeoutException) when (timeout is not null)
        {
            SafeLog(() => TelnyxRelayLog.TeardownTimedOut(
                _logger,
                _session?.CallId ?? "(before setup)",
                DisplayName(kind)));

            // Task.WaitAsync removes its own continuation from task once the timeout fires, rather
            // than leaving one behind to observe a later fault: a probe confirmed this directly. So
            // if task faults afterward with nobody else watching, that fault surfaces only as an
            // UnobservedTaskException raised during a GC — which produces no log line, and, under
            // the default ThrowUnobservedTaskExceptions setting, does not crash the process either.
            // Without a continuation of our own here, the real fault behind this timeout — a model
            // fault, a tool fault, or the ObjectDisposedException from the token source this method
            // is about to dispose — would stay exactly that unseen. This continuation is what makes
            // it visible instead. It does not await task, so it does not block; it can run inline on
            // this thread if task happens to fault in the narrow window between the timeout firing
            // and this line attaching it, which ExecuteSynchronously allows and which is harmless
            // either way; and it must not throw, which LogFault's own SafeLog guard now guarantees.
            _ = task.ContinueWith(
                completed => LogFault(kind, completed.Exception?.GetBaseException()),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        catch (RelayProtocolException protocol)
        {
            // Malformed JSON or an oversized frame: the relay broke the contract, not a defect this
            // endpoint owns. DetermineCloseStatus reads this same exception straight off the task
            // afterward for the close status, so this line only has to log it — every other
            // malformed-input line in this file (PromptBeforeSetup, FrameRefused,
            // MalformedInterruptFrame) already logs at Warning, and LogFault's generic path below
            // would otherwise report this at Error, the same level as an unhandled bug.
            SafeLog(() => TelnyxRelayLog.RelayProtocolViolation(
                _logger,
                _session?.CallId ?? "(before setup)",
                protocol.Message));
        }
        catch (WebSocketException socketFault)
            when (kind == ConnectionTask.ReadLoop
                && socketFault.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
        {
            // The call dropped with no close frame. Section "close statuses" of task 7: that is an
            // ordinary end of a call, not a fault, so it is worth a line but not an error one.
            SafeLog(() => TelnyxRelayLog.CallDroppedWithNoCloseFrame(_logger, _session?.CallId ?? "(before setup)"));
        }
        catch (Exception fault)
        {
            LogFault(kind, fault);
        }
    }

    /// <summary>Logs the fault of a read loop, a turn, or a write loop, whichever <paramref name="kind"/> names.</summary>
    /// <param name="kind">Which task faulted.</param>
    /// <param name="fault">The cause, or <see langword="null"/> when none was available.</param>
    /// <remarks>
    /// Called from <see cref="ObserveAsync"/> directly, and from the fault-only continuation it
    /// attaches on a timeout. The continuation runs with nothing above it to catch a throw, so the
    /// <see cref="SafeLog"/> guard inside is what keeps this method from ever throwing, not the
    /// caller.
    /// </remarks>
    private void LogFault(ConnectionTask kind, Exception? fault)
    {
        if (fault is null)
        {
            return;
        }

        var callId = _session?.CallId ?? "(before setup)";
        SafeLog(() =>
        {
            switch (kind)
            {
                case ConnectionTask.Turn:
                    TelnyxRelayLog.TurnFaulted(_logger, callId, fault);
                    break;

                case ConnectionTask.WriteLoop:
                    TelnyxRelayLog.WriteLoopFaulted(_logger, callId, fault);
                    break;

                case ConnectionTask.ReadLoop:
                default:
                    TelnyxRelayLog.ReadLoopFaulted(_logger, callId, fault);
                    break;
            }
        });
    }

    /// <summary>Runs one log call, and never lets it break the teardown sequence around it.</summary>
    /// <param name="log">The call to make, already bound to its logger and its arguments.</param>
    /// <remarks>
    /// <see cref="Microsoft.Extensions.Logging"/> aggregates and rethrows a provider's own fault, so
    /// even a line whose only job is to report a defect can itself throw. Every log call teardown
    /// makes — cancelling the token, the close, an observed fault, a teardown timeout — sits behind
    /// this guard for that reason; logging exists to help diagnose a defect, and must never become a
    /// second one that stops the store removal or the rest of teardown from running.
    /// </remarks>
    private static void SafeLog(Action log)
    {
        try
        {
            log();
        }
        catch (Exception)
        {
            // Nothing above this point can safely observe a logging fault either, so it stops here.
        }
    }

    /// <summary>Waits out the losing side of the idle race, then hands its buffer back to the pool.</summary>
    /// <param name="receiving">The receive the idle deadline, or teardown, won the race against.</param>
    /// <param name="buffer">
    /// The array <paramref name="receiving"/> still writes into until it completes. Ownership of
    /// returning it to <see cref="ArrayPool{T}.Shared"/> moves here, and here only, the moment
    /// <see cref="ReadLoopAsync"/> abandons this receive — <c>ReadLoopAsync</c>'s own <c>finally</c>
    /// must not also return it, or the pool would see it twice.
    /// </param>
    /// <remarks>
    /// <para>
    /// Nothing here awaits <paramref name="receiving"/> inline, and nothing may: the socket is
    /// about to receive a graceful close, and blocking on a receive that is still pending against
    /// a socket this connection no longer owns would either wait on a peer that already stopped
    /// sending or throw once the close races it.
    /// </para>
    /// <para>
    /// The buffer is returned from this continuation, never from <c>ReadLoopAsync</c>'s own
    /// <c>finally</c>, because that <c>finally</c> runs the instant <c>ReadLoopAsync</c> returns —
    /// while <paramref name="receiving"/> can still be mid-write into <paramref name="buffer"/>. A
    /// probe with two real sockets over a loopback TCP pair proved exactly that: returning early
    /// let a second renter receive the same array, and the first receive's own bytes then landed
    /// in it once the peer actually sent something, overwriting whatever the second renter had put
    /// there. <see cref="ArrayPool{T}.Shared"/> is shared with every other read loop in this
    /// process, including one on a completely different call, so that corruption is not confined
    /// to this connection. Deferring the return until <paramref name="receiving"/> itself finishes
    /// — successfully, faulted, or cancelled, it makes no difference — is what keeps the array out
    /// of the pool for exactly as long as something can still write to it, never shorter.
    /// </para>
    /// </remarks>
    private static void ObserveAbandonedReceive(Task<ValueWebSocketReceiveResult> receiving, byte[] buffer)
    {
        _ = receiving.ContinueWith(
            completed =>
            {
                // Marks a fault observed the same way the earlier, buffer-free version did, so it
                // never becomes an unobserved task exception, whatever receiving finished with.
                _ = completed.Exception;

                // clearArray: true. The array still holds the caller's own words, and the pool it
                // goes back to is shared with Kestrel and with every other read loop in this
                // process. D23 makes the audit chain the record of a call; a pooled buffer that
                // hands a transcript to the next renter is not a record, it is a leak.
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>Names one <see cref="ConnectionTask"/> for a log line, in the sentence it reads in.</summary>
    private static string DisplayName(ConnectionTask kind) => kind switch
    {
        ConnectionTask.ReadLoop => "the read loop",
        ConnectionTask.Turn => "the last turn",
        ConnectionTask.WriteLoop => "the write loop",
        _ => "a task",
    };

    private async Task ReadLoopAsync()
    {
        var rented = ArrayPool<byte>.Shared.Rent(4 * 1024);
        ArrayBufferWriter<byte> message = new(4 * 1024);

        // Set the moment an abandoned receive takes over returning `rented` — see
        // ObserveAbandonedReceive. Once true, the finally below must not also return it: the pool
        // would then hand the same array out twice at once, which corrupts it just as surely as
        // returning it too early does.
        var bufferOwnedByAbandonedReceive = false;

        try
        {
            while (!_cancellation.IsCancellationRequested)
            {
                message.ResetWrittenCount();
                ValueWebSocketReceiveResult result;

                // The vendor never reconnects, so a socket with no inbound frame for IdleTimeout is
                // a call that already ended, not a fault. The first receive of every message races
                // the deadline rather than being cancelled by one: a probe against a real Kestrel
                // WebSocket confirmed that cancelling ReceiveAsync's own token while it is pending
                // aborts the socket outright — WebSocketState.Aborted — which then makes the
                // graceful NormalClosure close below throw instead of reaching the vendor. Racing a
                // side task leaves the receive itself untouched; the side that loses the race is
                // simply abandoned rather than cancelled, exactly as a healthy send and a healthy
                // receive already run concurrently on this same socket during ordinary operation.
                //
                // idling is built first, deliberately: Task.Delay validates IdleTimeout and throws
                // synchronously — confirmed on net10 for a negative span, 60 days, and
                // TimeSpan.MaxValue — for anything MapTelnyxRelay's own startup check did not
                // catch. Building it before receiving exists means that throw can only ever find
                // no receive in flight yet, so the finally below stays free to return `rented`
                // unconditionally; building it after would leave a live receive, on
                // CancellationToken.None, stranded against an array the finally had already handed
                // back to the pool — the exact hazard fixed elsewhere in this method, reopened by a
                // bad option value instead of by teardown.
                using CancellationTokenSource idleCancel = CancellationTokenSource.CreateLinkedTokenSource(_cancellation.Token);
                var idling = Task.Delay(_options.IdleTimeout, _timeProvider, idleCancel.Token);
                var receiving = _socket.ReceiveAsync(rented.AsMemory(), CancellationToken.None).AsTask();

                if (await Task.WhenAny(receiving, idling).ConfigureAwait(false) != receiving)
                {
                    // idling won: either IdleTimeout actually elapsed, or _cancellation itself
                    // fired and cancelled this Task.Delay along with it — host stopping, or the
                    // request aborting. IsCompletedSuccessfully tells the two apart, since a
                    // cancelled Task.Delay never reaches that state, and only the elapsed case is
                    // this connection's own idle deadline rather than teardown asked for elsewhere.
                    // Either way, DetermineCloseStatus only reaches InternalServerError off
                    // reading.IsFaulted, and returning here rather than throwing leaves this task
                    // Completed, not Faulted, so both fall through its checks to the same
                    // NormalClosure a clean close already gets — or to EndpointUnavailable, when
                    // the host is the one stopping.
                    bufferOwnedByAbandonedReceive = true;
                    ObserveAbandonedReceive(receiving, rented);

                    if (idling.IsCompletedSuccessfully)
                    {
                        SafeLog(() => TelnyxRelayLog.IdleTimeoutReached(_logger, _session?.CallId ?? "(before setup)"));
                    }

                    return;
                }

                // The receive won the race. Cancelling idling's own token here, rather than
                // leaving it to expire on its own after IdleTimeout, releases the timer behind it
                // now instead of leaving one pending per message for as long as a busy call keeps
                // sending faster than IdleTimeout — CancelAfter is never involved, so this touches
                // nothing outside this one local source.
                idleCancel.Cancel();
                result = await receiving.ConfigureAwait(false);

                // The rest of one message, however many more fragments the vendor chose. A loop
                // that parsed each receive on its own would fail on a fragment, and it would cut a
                // multi-byte character in half. Only the first fragment above raced the idle
                // deadline: the vendor is, by definition, no longer silent once one fragment of a
                // message has already arrived.
                while (true)
                {
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return;
                    }

                    if (message.WrittenCount + result.Count > _options.MaxFrameBytes)
                    {
                        throw new RelayProtocolException(
                            WebSocketCloseStatus.MessageTooBig,
                            "the relay frame passes the size limit.");
                    }

                    message.Write(rented.AsSpan(0, result.Count));

                    if (result.EndOfMessage)
                    {
                        break;
                    }

                    result = await _socket
                        .ReceiveAsync(rented.AsMemory(), _cancellation.Token)
                        .ConfigureAwait(false);
                }

                // Parsed here, synchronously, before any await sees this iteration again. Passing
                // the parsed RelayFrame into DispatchAsync — rather than the bytes, which an async
                // method cannot take as a ReadOnlySpan<byte> parameter anyway — means nothing async
                // can ever alias the rented buffer or the ArrayBufferWriter this loop is about to
                // reset and reuse.
                if (!TelnyxRelayFrameReader.TryRead(
                    message.WrittenSpan,
                    out var frame,
                    out var unknownType,
                    out var refusedType))
                {
                    if (unknownType is null && refusedType is null)
                    {
                        // Neither name is set, so the bytes carried no readable type at all: not
                        // JSON, not an object, or a type that is not a string. That is the only
                        // shape this endpoint closes a socket over.
                        throw new RelayProtocolException(
                            WebSocketCloseStatus.InvalidPayloadData,
                            "the relay sent a frame this endpoint cannot parse.");
                    }

                    // Log once for the call, not once for the frame. Section 3.1.
                    if (unknownType is { } unmodelled)
                    {
                        if (!_loggedUnknownFrame)
                        {
                            _loggedUnknownFrame = true;
                            TelnyxRelayLog.UnknownFrameType(_logger, unmodelled, _session?.CallId ?? "(before setup)");
                        }
                    }
                    else if (!_loggedRefusedFrameBody)
                    {
                        // A known type whose body will not bind. Section 7.1 treats a vendor that
                        // changes a frame exactly as it treats one that adds a frame: the frame is
                        // refused, and the call goes on.
                        _loggedRefusedFrameBody = true;
                        TelnyxRelayLog.FrameBodyRefused(
                            _logger, refusedType!, _session?.CallId ?? "(before setup)");
                    }

                    continue;
                }

                await DispatchAsync(frame!).ConfigureAwait(false);
            }
        }
        finally
        {
            // Skipped when an abandoned receive is still live against `rented`: ObserveAbandonedReceive
            // owns the return in that case, deferred until that receive itself finishes, and this
            // method has no way to know that has happened yet — returning here too would hand the
            // same array to a second renter while the first receive can still write into it.
            if (!bufferOwnedByAbandonedReceive)
            {
                // clearArray: true, for the reason ObserveAbandonedReceive gives: this array holds
                // what the caller said, and the pool behind it is shared with the whole process.
                ArrayPool<byte>.Shared.Return(rented, clearArray: true);
            }
        }
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
    }

    private async Task StartTurnAsync(string text)
    {
        if (_session is not { } session)
        {
            // Log once for the call, not once for the frame. Same rule as the unknown-frame line.
            if (!_loggedPromptBeforeSetup)
            {
                _loggedPromptBeforeSetup = true;
                TelnyxRelayLog.PromptBeforeSetup(_logger);
            }

            return;
        }

        Task justFinished;
        lock (_turnLock)
        {
            if (_turnActive)
            {
                // The caller finished a second sentence before the agent spoke. There is no
                // interrupt frame for this, because the vendor never truncated anything, so there
                // is no heard text to record. IConversationPort throws on a second turn of one
                // call, and a throw here would drop it, so one prompt is held and runs once the
                // turn in flight ends. Item 6a of section 11 draws the line at one: a queue of
                // held speech would answer questions the caller has already moved past, so a
                // third prompt is dropped instead of queued behind the first.
                //
                // _turnActive, not _turn.IsCompleted: RunPendingPrompt runs inside RunTurnAsync's
                // own finally, strictly before that turn's Task itself transitions to completed, so
                // a check against IsCompleted here could see "no turn running" for a call whose own
                // RunPendingPrompt had already run and found nothing pending — losing this prompt
                // outright, since nothing would ever come back to run it. _turnActive is cleared by
                // RunPendingPrompt in that same lock acquisition instead, so this check and that
                // clear can never miss each other.
                if (_pendingPrompt is null)
                {
                    _pendingPrompt = text;
                    TelnyxRelayLog.PromptHeld(_logger, session.CallId);
                }
                else if (!_loggedPendingPromptDropped)
                {
                    // Log once for the call, not once for the frame. Same rule as the
                    // unknown-frame line.
                    _loggedPendingPromptDropped = true;
                    TelnyxRelayLog.PendingPromptDropped(_logger, session.CallId);
                }

                return;
            }

            justFinished = _turn;
            _turnActive = true;
        }

        // A call runs many turns, and _turn only ever holds the most recent one. Observing the
        // finished turn here is what stops a turn that faulted mid-call from being silently
        // replaced by the next one before anything looks at it. The in-flight guard above only
        // proved _turnActive was false, not that justFinished itself has completed: RunPendingPrompt
        // clears _turnActive from inside RunTurnAsync's own finally, strictly before that turn's
        // Task transitions to completed, so a moment can exist where _turnActive already reads false
        // while justFinished is still finishing the last step of its own async state machine. This
        // await can therefore still yield here, but only for as long as that tail takes to unwind —
        // microseconds — which is why it is safe to await inline rather than fire-and-forget. Only
        // the very last turn of the call still relies on RunAsync's own observation at teardown.
        await ObserveAsync(justFinished, ConnectionTask.Turn).ConfigureAwait(false);

        lock (_turnLock)
        {
            var turnId = Interlocked.Increment(ref _turnId);
            _turn = RunTurnAsync(session, text, turnId);
        }
    }

    private async Task RunTurnAsync(CallSession session, string text, long turnId)
    {
        try
        {
            await foreach (var update in session
                .RunTurnStreamingAsync(text, _cancellation.Token)
                .ConfigureAwait(false))
            {
                // This check narrows the race; it does not close it. It sits after the await on the
                // enumerator and before the await on the channel write, so it stops every update the
                // model produces *after* HandleInterrupt marks this turn interrupted. It cannot stop
                // an update already past this line at that moment — already in the channel, or
                // parked inside WriteAsync waiting for room — so the write loop's own gate,
                // immediately before its one SendAsync call, is what actually keeps a straggler off
                // the wire. Both gates read _interruptedTurnId, and every queued item still carries
                // the id it was written under, so the write loop can tell a straggler from a current
                // token without a lock. The check is against _interruptedTurnId, not _turnId: a
                // later, ordinary turn starting also moves _turnId, and that alone must not silence
                // a turn nothing actually interrupted.
                //
                // continue, not return: abandoning the enumeration here would dispose
                // CallSession's own iterator from the outside, which unwinds it past the code that
                // records the interruption on LastTurn rather than through it. Session.Interrupt
                // already asked the model call to stop; this loop still has to drain whatever
                // update that call already had in flight so CallSession reaches its own ending on
                // its own terms. Every update after the raised id is read and thrown away instead
                // of queued, which is what keeps the queue itself from growing on a barge-in.
                if (Interlocked.Read(ref _interruptedTurnId) == turnId)
                {
                    continue;
                }

                if (update.Text is { Length: > 0 } piece)
                {
                    // Marked before the write, not after it. Once a piece is committed to the
                    // channel the caller is going to hear it, and a barge-in that lands while this
                    // write waits on backpressure still belongs to this turn. The ids only ever
                    // increase here, because RunPendingPrompt starts the next turn from inside this
                    // turn's own finally, so a plain exchange cannot move this value backwards.
                    Interlocked.Exchange(ref _spokenTurnId, turnId);

                    await _outbound.Writer
                        .WriteAsync(new OutboundItem(turnId, new RelayToken(piece, Last: false)), _cancellation.Token)
                        .ConfigureAwait(false);
                }
            }

            if (Interlocked.Read(ref _interruptedTurnId) != turnId)
            {
                // The vendor closes a reply on last: true, and the sample uses an empty final
                // token when the stream ended with no trailing text. A barge-in already ended
                // this turn, so a closing frame now would speak a reply the caller cut off.
                await _outbound.Writer
                    .WriteAsync(new OutboundItem(turnId, new RelayToken(string.Empty, Last: true)), _cancellation.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // CallSession absorbs a barge-in's own cancellation internally and ends the turn
            // normally, so what reaches here is the connection's own teardown token instead. The
            // call itself goes on; only this turn stops.
        }
        finally
        {
            RunPendingPrompt();
        }
    }

    /// <summary>Starts the one prompt <see cref="StartTurnAsync"/> held while this turn ran.</summary>
    /// <remarks>
    /// Called from a turn's own <c>finally</c>, so it runs on whichever thread that turn happens
    /// to finish on — never the read loop, and strictly before the Task <see cref="RunTurnAsync"/>
    /// returns actually completes. Reading <see cref="_pendingPrompt"/>, clearing <see
    /// cref="_turnActive"/> or handing it to a new turn, and reassigning <see cref="_turn"/> all
    /// happen inside one lock acquisition, because a caller of <see cref="StartTurnAsync"/> that
    /// reads <c>_turnActive</c> as false must also see <see cref="_pendingPrompt"/> as null in that
    /// same instant — otherwise both this method and that caller could start a turn at once.
    /// </remarks>
    private void RunPendingPrompt()
    {
        lock (_turnLock)
        {
            // The connection is tearing down, so a fresh turn here would write to _outbound after
            // RunAsync completes it, and RunAsync's own read of _turn (also under this lock) may
            // already have captured the task this call is inside of — leaving a turn nothing waits
            // for. The caller's last sentence goes unanswered either way once the socket is gone.
            if (_pendingPrompt is not { } held
                || _session is not { } session
                || _cancellation.IsCancellationRequested)
            {
                _turnActive = false;
                return;
            }

            _pendingPrompt = null;
            var turnId = Interlocked.Increment(ref _turnId);
            _turn = RunTurnAsync(session, held, turnId);
        }
    }

    private void HandleInterrupt(RelayFrame.Interrupt interrupt)
    {
        if (_session is not { } session)
        {
            return;
        }

        // Section 7.1's rule for an unknown frame type and for a prompt before setup applies here
        // too: a frame the vendor got wrong must not drop the call. System.Text.Json enforces
        // neither a missing property nor a negative number at run time, so a frame missing
        // utteranceUntilInterrupt, or carrying a negative durationUntilInterruptMs, would otherwise
        // reach CallSession.Interrupt's own ArgumentNullException or ArgumentOutOfRangeException
        // uncaught and take the read loop down with it. Nothing here clamps the duration — a clamp
        // is an estimate, and D28 exists to keep estimates out of this path.
        if (interrupt.UtteranceUntilInterrupt is null || interrupt.DurationUntilInterruptMs < 0)
        {
            if (!_loggedMalformedInterrupt)
            {
                _loggedMalformedInterrupt = true;
                TelnyxRelayLog.MalformedInterruptFrame(_logger, session.CallId);
            }

            return;
        }

        bool cutsRunningTurn;
        lock (_turnLock)
        {
            // Mark before cancelling. Every project that solves this race does it in this order:
            // a token already past the guard in RunTurnAsync must find its own id already marked
            // interrupted right here, not a cancellation that is still on its way to the model
            // call.
            //
            // _spokenTurnId is what is marked, and never _turnId. The two differ exactly when a
            // held prompt fired: turn N finishes streaming, RunPendingPrompt starts turn N+1 inside
            // turn N's own finally, and Telnyx is still speaking turn N. _turnId then names N+1, a
            // turn the caller has not heard one word of, and marking it would silence it and skip
            // its closing frame. _spokenTurnId names the turn whose output actually reached the
            // relay, which is the turn the caller was hearing and the only one a barge-in may cut.
            //
            // Zero means no turn has produced a word yet, so there is nothing to cut short and the
            // mark stays where it was.
            var spoken = Interlocked.Read(ref _spokenTurnId);
            if (spoken != 0)
            {
                Interlocked.Exchange(ref _interruptedTurnId, spoken);
            }

            // The same comparison the mark above rests on, carried across the seam. CallSession
            // cannot reach it: it decides from whether the *running* turn is audible to it, and a
            // turn is audible to it the moment it hands over any content at all — a tool call, a
            // line of reasoning — none of which is a word the relay ever spoke. So turn N+1 can be
            // audible to the core while _spokenTurnId still names turn N, which is the turn Telnyx
            // is still playing and the only one the caller has heard. Deciding here, where both ids
            // are known and already under the lock that guards them, is what puts the heard text on
            // turn N instead of on a turn nobody has heard one word of.
            //
            // Zero answers false as well. Nothing of this call has reached the relay, so there is no
            // running turn the caller was hearing, and cutting one short would destroy a reply
            // against nothing — the same rule the mark above already follows by staying put.
            cutsRunningTurn = spoken != 0 && spoken == Interlocked.Read(ref _turnId);
        }

        // The vendor measured both values itself. D28 and item 6a: nothing here estimates either
        // one. A false result means there was no turn to record the barge-in against — not merely
        // that none was running, because a turn that already ended is amended rather than ignored.
        session.Interrupt(
            interrupt.UtteranceUntilInterrupt,
            TimeSpan.FromMilliseconds(interrupt.DurationUntilInterruptMs),
            cutsRunningTurn);

        // The relay already stopped its own audio. Conversation Relay carries no clear frame, so
        // anything still queued here is the only audio left that could start it playing again,
        // and this queue is the only thing this process still controls.
        while (_outbound.Reader.TryRead(out _))
        {
        }

        // Logged last, once the raised id, the call to Interrupt, and the drain above have all
        // already happened, so this line is proof the whole guard is in place — never the words
        // the caller said or heard.
        TelnyxRelayLog.InterruptReceived(_logger, session.CallId);
    }

    private async Task WriteLoopAsync()
    {
        await foreach (var item in _outbound.Reader.ReadAllAsync(_cancellation.Token).ConfigureAwait(false))
        {
            if (_socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
            {
                return;
            }

            // The write loop is the last gate, immediately before the one SendAsync call this
            // whole class makes. RunTurnAsync's own check only narrows the race — it cannot stop a
            // token already past it at the moment HandleInterrupt marks the id interrupted: one
            // already queued, one a bounded channel's backpressure was about to release into the
            // gap HandleInterrupt's own drain just made, or one this loop had already dequeued and
            // was about to send. All three read _interruptedTurnId, the same field the drain marks,
            // so whichever of them let a stale item this far, it stops here instead of reaching the
            // caller. The check is never against _turnId: an item from a turn that simply ended,
            // with a newer one already under way, is not stale, and _turnId alone cannot tell the
            // two apart.
            //
            // >=, not ==. _interruptedTurnId and every item's own TurnId both only ever increase, so
            // "my id is at or below the highest id a barge-in has cut off" is the question that
            // actually matches a call across two barge-ins: with == alone, a turn-N item already
            // dequeued here, then gated after a second, later barge-in raises the mark to N+1, would
            // compare N+1 != N, pass, and reach the caller — exactly the straggler this gate exists
            // to stop. With no interrupt at all _interruptedTurnId stays 0, and turnId is always at
            // least 1 once a turn exists, so 0 >= turnId is always false and nothing is dropped that
            // >= would not already drop with ==. The one behaviour change is a prior turn's trailing
            // last: true frame, still queued when a later turn gets interrupted: it now drops instead
            // of reaching the caller, which is exactly what HandleInterrupt's own unconditional drain
            // already does to that same frame whenever it is still queued at the moment of the
            // interrupt rather than sent moments later.
            if (item.TurnId is { } turnId && Interlocked.Read(ref _interruptedTurnId) >= turnId)
            {
                continue;
            }

            var bytes = JsonSerializer.SerializeToUtf8Bytes(item.Frame, item.Frame.GetType(), TelnyxRelayJson.Options);
            await _socket
                .SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, _cancellation.Token)
                .ConfigureAwait(false);
        }
    }

    private async Task CloseAsync(WebSocketCloseStatus status, string? description)
    {
        if (_socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
        {
            return;
        }

        // Sends serialize behind the WebSocket's own send mutex, so a close that overlaps a send
        // stuck on relay backpressure waits on it rather than throwing — and CancellationToken.None
        // would then wait forever. The connection token is already cancelled by the time this runs,
        // so the close gets its own bounded token instead.
        using CancellationTokenSource closeDeadline = new();
        closeDeadline.CancelAfter(_options.CloseTimeout);

        try
        {
            // CloseOutputAsync, never CloseAsync. CloseAsync waits for the peer close frame, and a
            // call that already dropped never sends one.
            await _socket
                .CloseOutputAsync(status, TruncateForCloseFrame(description), closeDeadline.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (closeDeadline.IsCancellationRequested)
        {
            // The relay stopped reading and applied backpressure, so the send behind the close
            // would never complete. Abort rather than leave the connection, the Kestrel request,
            // and the store entry alive forever.
            _socket.Abort();
        }
        catch (WebSocketException)
        {
            // The far end already went away. That is the end of a call, not a fault.
        }
    }

    /// <summary>Keeps a close description inside the 123-byte limit the close frame's control payload allows.</summary>
    /// <param name="description">The description a status carries, or null.</param>
    /// <returns>The description, cut short if needed, or null.</returns>
    /// <remarks>
    /// Every description this class ever passes is one of the two fixed, short, plain-English
    /// messages <see cref="RelayProtocolException"/> carries today, so a character cut is a safe
    /// stand-in for a byte-accurate one — this only guards a future message that grows past the
    /// limit, so <see cref="WebSocket.CloseOutputAsync"/> never throws instead of closing.
    /// </remarks>
    private static string? TruncateForCloseFrame(string? description)
        => description is { Length: > 100 } ? description[..100] : description;
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

/// <summary>One item queued for the write loop, carrying the turn id it was written under.</summary>
/// <param name="TurnId">
/// The turn that queued <see cref="Frame"/>, or <see langword="null"/> for a frame no turn owns —
/// nothing writes one of those yet, but the write loop's gate only applies when an id is present,
/// so a future frame outside the turn lifecycle (a close handoff, for one) can opt out by carrying
/// none. This type never reaches <see cref="TelnyxRelayJson"/>: only <see cref="Frame"/> is
/// serialized, so nothing here changes the wire.
/// </param>
/// <param name="Frame">The frame to serialize and send: a <see cref="RelayToken"/> today.</param>
internal readonly record struct OutboundItem(long? TurnId, object Frame);
