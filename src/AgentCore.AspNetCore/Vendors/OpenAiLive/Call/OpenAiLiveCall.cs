using System.Globalization;
using System.Text.Json.Nodes;
using AgentCore.Application.Conversation.Commands;
using AgentCore.Application.Hooks.Notices;
using AgentCore.AspNetCore.Calls;
using AgentCore.AspNetCore.Vendors.OpenAiLive.Wire;
using AgentCore.Domain.Audit;
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

        private readonly LiveChannel _channel;

        private readonly ILiveSideband _sideband;

        private readonly string? _greeting;

        private readonly LiveHearing _hearing;

        private readonly SemaphoreSlim _sending = new(1, 1);

        private readonly LiveAnswers _answers;

        private readonly LiveTransfer? _transfer;

        private readonly LiveCallFinish _finish;

        // Set off the loop (an answer, a timer) and acted on by the loop, which alone touches the ledger.
        private readonly LiveCallEnd _end;

        private readonly TaskCompletionSource _loopLeft = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _looping;

        private long _eventIds;

        private bool _started;

        /// <summary>
        /// <paramref name="channel"/> is the call's channel, already attached to its conversation. A
        /// <see langword="null"/> greeting leaves GPT-Live silent until the caller speaks. A <see langword="null"/>
        /// transfer line leaves the call unable to transfer, so its conversation answers a transfer as not supported.
        /// </summary>
        internal OpenAiLiveCall(
            PhoneCall call,
            LiveChannel channel,
            ILiveSideband sideband,
            Func<CancellationToken, Task<bool>> hangUp,
            ILogger logger,
            string? greeting = null,
            ILiveTransferLine? transferLine = null)
        {
            _call = call;
            _channel = channel;
            _sideband = sideband;
            Logger = logger;
            _greeting = greeting;
            _end = new LiveCallEnd(call.Host.Time, call.CallId, logger);
            _hearing = new LiveHearing(call);
            _transfer = transferLine is null
                ? null
                : new LiveTransfer(transferLine, () => _end.Finished || call.HasEnded, () => _ = TransferAfterAnswersAsync());
            _answers = new LiveAnswers(call, _end, SendAsync, NextEventId, () => call.EngineEnded || _transfer?.Target is not null, logger);
            _finish = new LiveCallFinish(call, _end, _answers, hangUp, logger);
            if (_transfer is not null)
            {
                channel.Transfers(_transfer);
            }
        }

        internal string CallId => _call.CallId;

        internal ILogger Logger { get; }

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

            try
            {
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
                        if (cause == AgentEndedCause && _transfer is { Pending: { } pending })
                        {
                            (receiving, bool goesOn) = await TransferAsync(
                                _transfer, pending, receiving is { IsCompleted: true } ? null : receiving, cancellationToken).ConfigureAwait(false);

                            if (goesOn)
                            {
                                continue;
                            }

                            return;
                        }

                        OpenAiLiveLog.HungUpBecause(Logger, CallId, cause);

                        await _finish.HangUpAndFinishAsync(reason, cause, cancellationToken).ConfigureAwait(false);

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

                await _finish.FinishAsync(ConversationEndReason.Faulted, SidebandClosedCause).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The host is stopping; OpenAiLiveCalls ends the call itself.
            }
            catch (Exception fault) when (fault is not OperationCanceledException)
            {
                OpenAiLiveLog.SidebandFaulted(Logger, CallId, fault);

                await _hearing.FlushAsync().ConfigureAwait(false);

                await _finish.FinishAsync(ConversationEndReason.Faulted, SidebandFailedCause).ConfigureAwait(false);
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

            await _finish.HangUpAndFinishAsync(reason, cause, cancellationToken).ConfigureAwait(false);
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

                case LiveEvent.Started when !_started:
                    _started = true;

                    await OpenAsync(cancellationToken).ConfigureAwait(false);

                    return false;

                case LiveEvent.Appended { EventId: { } eventId }:
                    _end.Acked(eventId);

                    return false;

                case LiveEvent.Closed end:
                    await _hearing.FlushAsync().ConfigureAwait(false);

                    await _finish.FinishAsync(ConversationEndReason.CallerHungUp, end.Reason ?? SidebandClosedCause).ConfigureAwait(false);

                    return true;

                case LiveEvent.Failed failed:
                    OpenAiLiveLog.LiveError(Logger, CallId, failed.Code ?? "unknown", failed.Message ?? string.Empty);

                    return false;

                default:
                    return false;
            }
        }

        private async ValueTask OpenAsync(CancellationToken cancellationToken)
        {
            // GPT-Live knows no date of its own: on a real call it named the wrong weekday.
            if (_call.Session.Runner.ClockLine() is { } clock)
            {
                await SendAsync(OpenAiLiveWire.SessionFact(NextEventId(), clock), cancellationToken).ConfigureAwait(false);
            }

            await _channel.OpenAsync(TellAsync, cancellationToken).ConfigureAwait(false);

            if (_greeting is not null)
            {
                await SendAsync(OpenAiLiveWire.GreetFirst(NextEventId(), _greeting), cancellationToken).ConfigureAwait(false);
            }
        }

        private ValueTask TellAsync(string context, CancellationToken cancellationToken)
        {
            return SendAsync(OpenAiLiveEvents.SessionThinking(context, NextEventId()), cancellationToken);
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
        private ValueTask HearReceivedAsync(Task<string?>? receiving)
        {
            return receiving is { IsCompletedSuccessfully: true, Result: { } json } && LiveEventReader.Read(json) is LiveEvent.Transcript delta
                ? _hearing.HearAsync(delta)
                : ValueTask.CompletedTask;
        }

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

        private async Task<(Task<string?>? Receiving, bool GoesOn)> TransferAsync(
            LiveTransfer transfer, TransferCommand pending, Task<string?>? receiving, CancellationToken cancellationToken)
        {
            OpenAiLiveLog.Transferring(Logger, CallId);

            LiveHandover handover = await transfer.Line
                .HandOverAsync(pending, new LivePeerClose(_sideband, _hearing, _call.Host.Time), receiving, cancellationToken).ConfigureAwait(false);

            switch (handover.Outcome)
            {
                case LiveHandoverOutcome.Taken:
                    await _finish.HangUpAndFinishAsync(ConversationEndReason.TransferredToHuman, TransferredCause, cancellationToken).ConfigureAwait(false);

                    return (handover.Receiving, false);

                case LiveHandoverOutcome.TakenAndClosed:
                    await _finish.FinishAsync(ConversationEndReason.TransferredToHuman, TransferredCause).ConfigureAwait(false);

                    return (null, false);

                case LiveHandoverOutcome.NotTaken:
                default:
                    bool goesOn = await FailedTransferAsync(transfer, handover.Why ?? "the line did not take the call", cancellationToken).ConfigureAwait(false);

                    return (handover.Receiving, goesOn);
            }
        }

        // With the host's words for a failed transfer, GPT-Live says them and the call goes on: the end that started the
        // transfer is spent, so the loop listens for the next one. An engine end that came meanwhile hangs up after them.
        private async Task<bool> FailedTransferAsync(LiveTransfer transfer, string why, CancellationToken cancellationToken)
        {
            if (transfer.IfFailed is not { } words || _call.HasEnded)
            {
                OpenAiLiveLog.TransferFailed(Logger, CallId, why);

                await _finish.HangUpAndFinishAsync(ConversationEndReason.Faulted, TransferFailedCause, cancellationToken).ConfigureAwait(false);

                return false;
            }

            OpenAiLiveLog.TransferFailedGoingOn(Logger, CallId, why);

            transfer.Failed();

            _end.Rearm();

            await SendAsync(OpenAiLiveWire.SayNow(NextEventId(), words), cancellationToken).ConfigureAwait(false);

            if (_call.EngineEnded)
            {
                _end.WhenQuiet();
            }

            return true;
        }

        private string NextEventId()
        {
            return "ac" + Interlocked.Increment(ref _eventIds).ToString(CultureInfo.InvariantCulture);
        }
    }
}
