using System.Globalization;
using System.Text.Json.Nodes;
using AgentCore.Application.Hooks.Notices;
using AgentCore.Application.Transcript;
using AgentCore.AspNetCore.Calls;
using AgentCore.AspNetCore.Vendors.OpenAiLive.Wire;
using AgentCore.Domain.Audit;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AgentCore.AspNetCore.Vendors.OpenAiLive.Call
{
    /// <summary>
    /// One live GPT-Live call: reads the sideband, keeps the transcript ledger, and answers each delegation through the
    /// call core. Talks to the engine only through <see cref="PhoneCall"/>.
    /// </summary>
    internal sealed class OpenAiLiveCall : IDisposable
    {
        internal const string ShutdownCause = "shutdown";

        internal const string SidebandClosedCause = "sideband_closed";

        internal const string SidebandFailedCause = "sideband_failed";

        internal const string AttachFailedCause = "attach_failed";

        internal const string AgentEndedCause = "agent_ended";

        internal const string ConversationLostCause = "conversation_lost";

        internal const string TransferredCause = "transferred";

        internal const string TransferFailedCause = "transfer_failed";

        /// <summary>How long after its send the last answer piece may go unacked before an engine-ended call hangs up anyway.</summary>
        internal static readonly TimeSpan AckWait = TimeSpan.FromSeconds(5);

        /// <summary>
        /// How long GPT-Live's speech must stay quiet after the ack before an engine-ended call hangs up. The ack marks
        /// the start of speech (p1-a), and no probe log has a speech-done event.
        /// </summary>
        internal static readonly TimeSpan EndQuietWait = TimeSpan.FromSeconds(2);

        private readonly PhoneCall _call;

        private readonly ILiveSideband _sideband;

        private readonly Func<CancellationToken, Task<bool>> _hangUp;

        private readonly ILogger _logger;

        private readonly string? _greeting;

        private readonly LiveHearing _hearing;

        private readonly SemaphoreSlim _sending = new(1, 1);

        private readonly LiveAnswers _answers;

        private readonly LiveTransfer? _transfer;

        // Set off the loop (an answer, a timer) and acted on by the loop, which alone touches the ledger.
        private readonly LiveCallEnd _end;

        private readonly TaskCompletionSource _loopLeft = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _looping;

        private int _hungUp;

        private long _eventIds;

        /// <summary>
        /// A <see langword="null"/> greeting leaves GPT-Live silent until the caller speaks. A <see langword="null"/>
        /// refer leaves the call unable to transfer, so its conversation answers a transfer as not supported.
        /// </summary>
        internal OpenAiLiveCall(
            PhoneCall call,
            ILiveSideband sideband,
            Func<CancellationToken, Task<bool>> hangUp,
            ILogger logger,
            string? greeting = null,
            Func<Uri, CancellationToken, Task<bool>>? refer = null,
            TimeSpan? transferWait = null)
        {
            _call = call;
            _sideband = sideband;
            _hangUp = hangUp;
            _logger = logger;
            _greeting = greeting;
            _end = new LiveCallEnd(call.Host.Time, call.CallId, logger);
            _hearing = new LiveHearing(call);
            _transfer = refer is null
                ? null
                : new LiveTransfer(refer, transferWait ?? OpenAiLiveSettings.DefaultTransferWait, () => _end.Finished || call.HasEnded, () => _ = TransferAfterAnswersAsync());
            _answers = new LiveAnswers(call, _end, SendAsync, NextEventId, () => call.EngineEnded || _transfer?.Target is not null, logger);
        }

        internal string CallId => _call.CallId;

        internal ILogger Logger => _logger;

        /// <summary>Disposes the send lock and the end timers. <see cref="RunAsync"/> calls this once every answer is done.</summary>
        public void Dispose()
        {
            _sending.Dispose();
            _end.Dispose();
        }

        internal async Task RunAsync(CancellationToken cancellationToken)
        {
            Task<string?>? receiving = null;
            Volatile.Write(ref _looping, 1);
            _ = HangUpAfterAnyEngineEndAsync();
            if (_transfer is not null)
            {
                _call.Channel.Use(_transfer, _call.Session);
            }

            try
            {
                // GPT-Live knows no date of its own: on a real call it named the wrong weekday.
                if (_call.Session.Runner.ClockLine() is { } clock)
                {
                    await SendAsync(OpenAiLiveWire.SessionFact(NextEventId(), clock), cancellationToken).ConfigureAwait(false);
                }

                if (_greeting is not null)
                {
                    await SendAsync(OpenAiLiveWire.GreetFirst(NextEventId(), _greeting), cancellationToken).ConfigureAwait(false);
                }

                while (true)
                {
                    receiving ??= _sideband.ReceiveAsync(cancellationToken).AsTask();
                    _ = await Task.WhenAny(receiving, _end.Requested).ConfigureAwait(false);
                    if (_end.Requested.IsCompleted)
                    {
                        (ConversationEndReason reason, string cause) = await _end.Requested.ConfigureAwait(false);
                        await HearReceivedAsync(receiving).ConfigureAwait(false);
                        await _hearing.FlushAsync().ConfigureAwait(false);

                        // The quiet wait after the last answer ends in the transfer when one waits, in place of the hang-up.
                        if (cause == AgentEndedCause && _transfer is { Target: { } target })
                        {
                            receiving = await TransferAsync(_transfer, target, receiving is { IsCompleted: true } ? null : receiving, cancellationToken).ConfigureAwait(false);
                            return;
                        }

                        OpenAiLiveLog.HungUpBecause(_logger, CallId, cause);
                        await HangUpAndFinishAsync(reason, cause, cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    string? json = await receiving.ConfigureAwait(false);
                    receiving = null;
                    if (json is null)
                    {
                        break;
                    }

                    if (await HandleAsync(LiveEventReader.Read(json), cancellationToken).ConfigureAwait(false))
                    {
                        return;
                    }
                }

                await _hearing.FlushAsync().ConfigureAwait(false);
                await FinishAsync(ConversationEndReason.Faulted, SidebandClosedCause).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The host is stopping; OpenAiLiveCalls ends the call itself.
            }
            catch (Exception fault) when (fault is not OperationCanceledException)
            {
                OpenAiLiveLog.SidebandFaulted(_logger, CallId, fault);
                await _hearing.FlushAsync().ConfigureAwait(false);
                await FinishAsync(ConversationEndReason.Faulted, SidebandFailedCause).ConfigureAwait(false);
            }
            finally
            {
                _ = _loopLeft.TrySetResult();
                await _answers.WhenAnsweredAsync().ConfigureAwait(false);
                Dispose();
                await _sideband.DisposeAsync().ConfigureAwait(false);

                // A receive left behind by an end ends with the socket; nothing reads its result.
                _ = receiving?.ContinueWith(static left => left.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            }
        }

        /// <summary>
        /// Hangs the call up from this side, then ends it, for host shutdown. A running loop ends the call
        /// itself, as for any other end, so its open line is heard first; a loop still short of that end when
        /// <paramref name="cancellationToken"/> fires is cut off, and the call is ended from here.
        /// </summary>
        internal async Task EndAsync(ConversationEndReason reason, string cause, CancellationToken cancellationToken)
        {
            _end.Now(reason, cause);
            if (Volatile.Read(ref _looping) == 1)
            {
                try
                {
                    await _loopLeft.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // The host stopped waiting: end the call anyway.
                }
            }

            await HangUpAndFinishAsync(reason, cause, cancellationToken).ConfigureAwait(false);
        }

        private async ValueTask<bool> HandleAsync(LiveEvent liveEvent, CancellationToken cancellationToken)
        {
            switch (liveEvent)
            {
                case LiveEvent.Transcript delta:
                    if (delta.Speaker == Speaker.Agent)
                    {
                        _end.AgentSpoke();
                    }

                    await _hearing.HearAsync(delta).ConfigureAwait(false);
                    return false;

                case LiveEvent.Delegation delegation:
                    (IReadOnlyList<LiveLine> before, string words) = await _hearing.TakeForDelegationAsync().ConfigureAwait(false);

                    // Once a transfer waits, no new turn starts: the caller is leaving for the line.
                    if (_transfer?.Target is null)
                    {
                        _answers.Start(delegation.Id, before, words, cancellationToken);
                    }

                    return false;

                case LiveEvent.Appended { EventId: { } eventId }:
                    _end.Acked(eventId);
                    return false;

                case LiveEvent.Closed end:
                    await _hearing.FlushAsync().ConfigureAwait(false);
                    await FinishAsync(ConversationEndReason.CallerHungUp, end.Reason ?? SidebandClosedCause).ConfigureAwait(false);
                    return true;

                case LiveEvent.Failed failed:
                    OpenAiLiveLog.LiveError(_logger, CallId, failed.Code ?? "unknown", failed.Message ?? string.Empty);
                    return false;

                default:
                    return false;
            }
        }

        // An end from outside every delegation (an operator's, the host's) has no answer of its own to hang up after.
        // Once the delegations in flight are answered, a call none of them set to end hangs up when GPT-Live's speech
        // has been quiet for EndQuietWait. The session is read once: an unload during the call is not followed.
        private async Task HangUpAfterAnyEngineEndAsync()
        {
            await _call.Session.Lifetime.Ending.WhenRequested.ConfigureAwait(false);
            await _answers.WhenAnsweredAsync().ConfigureAwait(false);
            _end.WhenQuiet();
        }

        // A transfer asked inside a delegation is set to leave after that answer; one asked from outside has no answer
        // of its own, so it leaves once GPT-Live went quiet, as an engine end from outside does.
        private async Task TransferAfterAnswersAsync()
        {
            await _answers.WhenAnsweredAsync().ConfigureAwait(false);
            _end.WhenQuiet();
        }

        // A message that arrived together with the end is still part of the call: its words are heard. A delegation in
        // it is not answered, and the ledger flush after this closes the line it would have closed.
        private ValueTask HearReceivedAsync(Task<string?>? receiving) =>
            receiving is { IsCompletedSuccessfully: true, Result: { } json } && LiveEventReader.Read(json) is LiveEvent.Transcript delta
                ? _hearing.HearAsync(delta)
                : ValueTask.CompletedTask;

        private async ValueTask SendAsync(JsonObject liveEvent, CancellationToken cancellationToken)
        {
            await _sending.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Nothing goes out once the call ended: the socket may already be gone.
                if (!_end.Finished)
                {
                    await _sideband.SendAsync(liveEvent.ToJsonString(), cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                _ = _sending.Release();
            }
        }

        // OpenAI never reports how a REFER went (probe T1, docs/probes/live-transfer-t1): the REST answer is 200 even
        // when the peer declines, and no event follows. The peer hangs the AI leg up once the line took the call, so a
        // close inside the wait is the transfer, and a call still up after it is a failed one. The caller's words are
        // still heard meanwhile, but start no turn. Returns the receive still open, for the loop's cleanup.
        private async Task<Task<string?>?> TransferAsync(LiveTransfer transfer, Uri target, Task<string?>? receiving, CancellationToken cancellationToken)
        {
            OpenAiLiveLog.Transferring(_logger, CallId);
            if (!await transfer.ReferAsync(target, _logger, CallId, cancellationToken).ConfigureAwait(false))
            {
                OpenAiLiveLog.TransferFailed(_logger, CallId, "the refer was refused");
                await HangUpAndFinishAsync(ConversationEndReason.Faulted, TransferFailedCause, cancellationToken).ConfigureAwait(false);
                return receiving;
            }

            using CancellationTokenSource waitStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task wait = Task.Delay(transfer.Wait, _call.Host.Time, waitStop.Token);
            try
            {
                while (true)
                {
                    receiving ??= _sideband.ReceiveAsync(cancellationToken).AsTask();
                    if (await Task.WhenAny(receiving, wait).ConfigureAwait(false) == wait)
                    {
                        await _hearing.FlushAsync().ConfigureAwait(false);

                        OpenAiLiveLog.TransferFailed(_logger, CallId, "the call was still up when the transfer wait ran out");

                        await HangUpAndFinishAsync(ConversationEndReason.Faulted, TransferFailedCause, cancellationToken).ConfigureAwait(false);

                        return receiving;
                    }

                    string? json = await receiving.ConfigureAwait(false);
                    receiving = null;
                    LiveEvent? liveEvent = json is null ? null : LiveEventReader.Read(json);

                    if (liveEvent is LiveEvent.Transcript delta)
                    {
                        await _hearing.HearAsync(delta).ConfigureAwait(false);
                    }
                    else if (json is null || liveEvent is LiveEvent.Closed)
                    {
                        await _hearing.FlushAsync().ConfigureAwait(false);

                        await FinishAsync(ConversationEndReason.TransferredToHuman, TransferredCause).ConfigureAwait(false);

                        return null;
                    }
                }
            }
            finally
            {
                await waitStop.CancelAsync().ConfigureAwait(false);
            }
        }

        private async Task HangUpAndFinishAsync(ConversationEndReason reason, string cause, CancellationToken cancellationToken)
        {
            if (!_end.Finished && Interlocked.Exchange(ref _hungUp, 1) == 0)
            {
                bool hungUp;
                try
                {
                    hungUp = await _hangUp(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception fault) when (fault is not OutOfMemoryException)
                {
                    OpenAiLiveLog.HangupFaulted(_logger, CallId, fault);
                    hungUp = true;
                }

                if (!hungUp)
                {
                    OpenAiLiveLog.HangupFailed(_logger, CallId);
                }
            }

            await FinishAsync(reason, cause).ConfigureAwait(false);
        }

        private async Task FinishAsync(ConversationEndReason reason, string cause)
        {
            if (!_end.TryFinish())
            {
                return;
            }

            await _call.EndAsync(reason, cause).ConfigureAwait(false);
            await _answers.WhenAnsweredAsync().ConfigureAwait(false);
            
            try
            {
                await _call.CloseAsync().ConfigureAwait(false);
            }
            // The close takes no token, so a cancellation here is the store's own timeout.
            catch (Exception fault) when (fault is not OutOfMemoryException)
            {
                OpenAiLiveLog.CloseFaulted(_logger, CallId, fault);
            }
        }

        private string NextEventId() => "ac" + Interlocked.Increment(ref _eventIds).ToString(CultureInfo.InvariantCulture);
    }
}
