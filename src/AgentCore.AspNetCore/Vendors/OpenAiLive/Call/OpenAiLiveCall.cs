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
    /// call core. Talks to the engine only through <see cref="PhoneCall"/>. A <see langword="null"/> greeting leaves GPT-Live
    /// silent until the caller speaks.
    /// </summary>
    internal sealed class OpenAiLiveCall(
        PhoneCall call, ILiveSideband sideband, Func<CancellationToken, Task<bool>> hangUp, ILogger logger, string? greeting = null) : IDisposable
    {
        internal const string ShutdownCause = "shutdown";

        internal const string SidebandClosedCause = "sideband_closed";

        internal const string SidebandFailedCause = "sideband_failed";

        internal const string AttachFailedCause = "attach_failed";

        internal const string AgentEndedCause = "agent_ended";

        internal const string ConversationLostCause = "conversation_lost";

        internal const string NothingNew = "The caller said nothing new since the last answer.";

        /// <summary>How long after its send the last answer piece may go unacked before an engine-ended call hangs up anyway.</summary>
        internal static readonly TimeSpan AckWait = TimeSpan.FromSeconds(5);

        /// <summary>
        /// How long GPT-Live's speech must stay quiet after the ack before an engine-ended call hangs up. The ack marks
        /// the start of speech (p1-a), and no probe log has a speech-done event.
        /// </summary>
        internal static readonly TimeSpan EndQuietWait = TimeSpan.FromSeconds(2);

        private readonly LiveTranscriptLedger _ledger = new();

        private readonly SemaphoreSlim _sending = new(1, 1);

        private readonly List<Task> _answers = [];

        // Set off the loop (an answer, a timer) and acted on by the loop, which alone touches the ledger.
        private readonly LiveCallEnd _end = new(call.Host.Time, call.CallId, logger);

        private readonly TaskCompletionSource _loopLeft = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _looping;

        private int _hungUp;

        private long _eventIds;

        internal string CallId => call.CallId;

        internal ILogger Logger => logger;

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
                // GPT-Live knows no date of its own: on a real call it named the wrong weekday.
                if (call.Session.Runner.ClockLine() is { } clock)
                {
                    await SendAsync(OpenAiLiveWire.SessionFact(NextEventId(), clock), cancellationToken).ConfigureAwait(false);
                }

                if (greeting is not null)
                {
                    await SendAsync(OpenAiLiveWire.GreetFirst(NextEventId(), greeting), cancellationToken).ConfigureAwait(false);
                }

                while (true)
                {
                    receiving ??= sideband.ReceiveAsync(cancellationToken).AsTask();
                    _ = await Task.WhenAny(receiving, _end.Requested).ConfigureAwait(false);
                    if (_end.Requested.IsCompleted)
                    {
                        (ConversationEndReason reason, string cause) = await _end.Requested.ConfigureAwait(false);
                        await HearReceivedAsync(receiving).ConfigureAwait(false);
                        await HearAsync(_ledger.Flush()).ConfigureAwait(false);
                        OpenAiLiveLog.HungUpBecause(logger, CallId, cause);
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

                await HearAsync(_ledger.Flush()).ConfigureAwait(false);
                await FinishAsync(ConversationEndReason.Faulted, SidebandClosedCause).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The host is stopping; OpenAiLiveCalls ends the call itself.
            }
            catch (Exception fault) when (fault is not OperationCanceledException)
            {
                OpenAiLiveLog.SidebandFaulted(logger, CallId, fault);
                await HearAsync(_ledger.Flush()).ConfigureAwait(false);
                await FinishAsync(ConversationEndReason.Faulted, SidebandFailedCause).ConfigureAwait(false);
            }
            finally
            {
                _ = _loopLeft.TrySetResult();
                await WhenAnsweredAsync().ConfigureAwait(false);
                Dispose();
                await sideband.DisposeAsync().ConfigureAwait(false);

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

                    await HearAsync(_ledger.Add(delta)).ConfigureAwait(false);
                    return false;

                case LiveEvent.Delegation delegation:
                    (IReadOnlyList<LiveLine> closed, IReadOnlyList<LiveLine> before, string words) = _ledger.TakeForDelegation();
                    await HearAsync(closed).ConfigureAwait(false);
                    lock (_answers)
                    {
                        _answers.Add(AnswerAsync(delegation.Id, before, words, cancellationToken));
                    }

                    return false;

                case LiveEvent.Appended { EventId: { } eventId }:
                    _end.Acked(eventId);
                    return false;

                case LiveEvent.Closed end:
                    await HearAsync(_ledger.Flush()).ConfigureAwait(false);
                    await FinishAsync(ConversationEndReason.CallerHungUp, end.Reason ?? SidebandClosedCause).ConfigureAwait(false);
                    return true;

                case LiveEvent.Failed failed:
                    OpenAiLiveLog.LiveError(logger, CallId, failed.Code ?? "unknown", failed.Message ?? string.Empty);
                    return false;

                default:
                    return false;
            }
        }

        // AskAsync runs before this method's first await: asks take their place in arrival order. The call
        // never ends from inside the ask (EndAsync would wait up to CallAsks.OlderAskWait on the ask itself), and never
        // from this task: the loop ends it, after its ledger flush.
        private async Task AnswerAsync(string delegationId, IReadOnlyList<LiveLine> before, string words, CancellationToken cancellationToken)
        {
            try
            {
                if (words.Length == 0)
                {
                    await SendAsync(OpenAiLiveEvents.Thinking(delegationId, NothingNew, NextEventId()), cancellationToken).ConfigureAwait(false);
                    if (call.EngineEnded)
                    {
                        EndNow(ConversationEndReason.AgentCompleted, AgentEndedCause);
                    }

                    return;
                }

                CallAnswer answer = await call.Asks.AskAsync(
                    delegationId,
                    words,
                    [.. before.Select(line => line.Speaker == Speaker.Caller ? new ChatMessage(ChatRole.User, line.Text) : FrontVoice.Line(line.Text))],
                    tool => SendAsync(OpenAiLiveEvents.Thinking(delegationId, $"The backend is running the tool {tool}. No answer yet.", NextEventId()), cancellationToken),
                    cancellationToken).ConfigureAwait(false);

                if (answer.Kind == CallAnswerKind.Withdrawn)
                {
                    return;
                }

                await SpeakAsync(delegationId, answer.Text, endAfter: call.EngineEnded, cancellationToken).ConfigureAwait(false);
            }
            catch (CallConversationLostException)
            {
                EndNow(ConversationEndReason.Faulted, ConversationLostCause);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The host is stopping.
            }
            catch (Exception fault) when (fault is not OperationCanceledException)
            {
                OpenAiLiveLog.AnswerFaulted(logger, CallId, fault);
            }
        }

        // Once the engine ended the conversation, the call hangs up after GPT-Live acked the last piece and
        // then went quiet for EndQuietWait, or AckWait after that piece's send with no ack.
        private async Task SpeakAsync(string delegationId, string text, bool endAfter, CancellationToken cancellationToken)
        {
            IReadOnlyList<string> pieces = CommentaryPieces.Split(text);
            if (endAfter && pieces.Count == 0)
            {
                EndNow(ConversationEndReason.AgentCompleted, AgentEndedCause);
                return;
            }

            for (int index = 0; index < pieces.Count; index++)
            {
                string eventId = NextEventId();
                if (endAfter && index == pieces.Count - 1)
                {
                    _end.AfterAck(eventId);
                }

                await SendAsync(OpenAiLiveEvents.Commentary(delegationId, pieces[index], eventId), cancellationToken).ConfigureAwait(false);
            }
        }

        // An end from outside every delegation (an operator's, the host's) has no answer of its own to hang up after.
        // Once the delegations in flight are answered, a call none of them set to end hangs up when GPT-Live's speech
        // has been quiet for EndQuietWait. The session is read once: an unload during the call is not followed.
        private async Task HangUpAfterAnyEngineEndAsync()
        {
            await call.Session.Lifetime.Ending.WhenRequested.ConfigureAwait(false);
            await WhenAnsweredAsync().ConfigureAwait(false);
            _end.WhenQuiet();
        }

        private void EndNow(ConversationEndReason reason, string cause) => _end.Now(reason, cause);

        // A message that arrived together with the end is still part of the call: its words are heard. A delegation in
        // it is not answered, and the ledger flush after this closes the line it would have closed.
        private ValueTask HearReceivedAsync(Task<string?>? receiving) =>
            receiving is { IsCompletedSuccessfully: true, Result: { } json } && LiveEventReader.Read(json) is LiveEvent.Transcript delta
                ? HearAsync(_ledger.Add(delta))
                : ValueTask.CompletedTask;

        private async ValueTask SendAsync(JsonObject liveEvent, CancellationToken cancellationToken)
        {
            await _sending.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Nothing goes out once the call ended: the socket may already be gone.
                if (!_end.Finished)
                {
                    await sideband.SendAsync(liveEvent.ToJsonString(), cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                _ = _sending.Release();
            }
        }

        private async ValueTask HearAsync(IReadOnlyList<LiveLine> lines)
        {
            foreach (LiveLine line in lines)
            {
                await call.HeardAsync(
                    line.Speaker,
                    line.Text,
                    call.StartedAt.AddMilliseconds(line.StartMs),
                    call.StartedAt.AddMilliseconds(line.EndMs),
                    call.Asks.AnsweredTurn).ConfigureAwait(false);
            }
        }

        private async Task HangUpAndFinishAsync(ConversationEndReason reason, string cause, CancellationToken cancellationToken)
        {
            if (!_end.Finished && Interlocked.Exchange(ref _hungUp, 1) == 0)
            {
                bool hungUp;
                try
                {
                    hungUp = await hangUp(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception fault) when (fault is not OutOfMemoryException)
                {
                    OpenAiLiveLog.HangupFaulted(logger, CallId, fault);
                    hungUp = true;
                }

                if (!hungUp)
                {
                    OpenAiLiveLog.HangupFailed(logger, CallId);
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

            await call.EndAsync(reason, cause).ConfigureAwait(false);
            await WhenAnsweredAsync().ConfigureAwait(false);
            try
            {
                await call.CloseAsync().ConfigureAwait(false);
            }
            // The close takes no token, so a cancellation here is the store's own timeout.
            catch (Exception fault) when (fault is not OutOfMemoryException)
            {
                OpenAiLiveLog.CloseFaulted(logger, CallId, fault);
            }
        }

        private Task WhenAnsweredAsync()
        {
            lock (_answers)
            {
                return Task.WhenAll(_answers);
            }
        }

        private string NextEventId() => "ac" + Interlocked.Increment(ref _eventIds).ToString(CultureInfo.InvariantCulture);
    }
}
