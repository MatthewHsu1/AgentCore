// Portions derived from LiveKit Agents, livekit-agents/livekit/agents/voice/agent_activity.py:3833-3863
// (_pipeline_reply_task_impl, the interrupted reply's forwarded_text) and generation.py:737-768 (forward_generation,
// the interrupted branch's wait for playout), commit d8405f132e1bd960f298190c18daf81ffc1faf45.
// Copyright 2023 LiveKit, Inc. Licensed under the Apache License, Version 2.0. Modified: translated to C#; the
// transport, not an audio sink, reports what played, and the reply's engine turn is cut rather than a chat
// context written.

using AgentCore.Application.Runtime;

namespace AgentCore.AspNetCore.Voice
{
    /// <summary>What the caller heard of one pipeline reply, and the cut its engine turn takes from it.</summary>
    /// <remarks>
    /// With nothing forwarded the turn keeps the caller's message only; with text forwarded it keeps what the
    /// transport reported the caller heard, or, with no report behind the interruption, the text forwarded, as
    /// LiveKit keeps <c>forwarded_text</c>. A final prompt that cut a step mid-way may be followed by the report:
    /// the reply waits for it, bounded, and recuts its turn to it.
    /// </remarks>
    /// <param name="stream">The engine turn the cut reaches.</param>
    /// <param name="time">The clock the wait for the report runs on.</param>
    /// <param name="heardTextWait">How long a reply a final prompt cut mid-step waits for the transport's report.</param>
    /// <param name="transportToken">Cancelled once, when the transport goes away and no report can come.</param>
    internal sealed class ReplyHearing(
        EngineReplyStream stream,
        TimeProvider time,
        TimeSpan heardTextWait,
        CancellationToken transportToken)
    {
        private static readonly TurnCut NothingHeard = new(string.Empty, Played: null);

        private readonly Lock _gate = new();

        private readonly TaskCompletionSource _bargeRecorded = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private string _forwarded = string.Empty;

        private string _beforeLastSpokenStep = string.Empty;

        private (string HeardText, TimeSpan PlayedDuration)? _barge;

        private volatile bool _forwardedText;

        private bool _cutMidStep;

        private bool _expectsBarge;

        private bool _bargeWaitOver;

        private bool _cutSettled;

        /// <summary>Gets whether the reply has handed any text to the output yet.</summary>
        public bool HasForwardedText => _forwardedText;

        /// <summary>Gets whether a final prompt interrupted the reply and it still takes the transport's report.</summary>
        public bool AwaitsBarge
        {
            get
            {
                lock (_gate)
                {
                    return _expectsBarge && !_bargeWaitOver;
                }
            }
        }

        /// <summary>Marks the interruption about to land as a final prompt's, which the transport's report may follow.</summary>
        public void ExpectBarge()
        {
            lock (_gate)
            {
                _expectsBarge = true;
            }
        }

        /// <summary>Keeps what the transport reported for the barge-in that interrupts, or interrupted, the reply.</summary>
        /// <param name="heardText">The text the caller heard.</param>
        /// <param name="playedDuration">How much of the reply played.</param>
        /// <returns>
        /// <see langword="true"/> for the first report, which is the one kept; <see langword="false"/> for a later one,
        /// or for one that comes once the reply stopped waiting for it.
        /// </returns>
        public bool RecordBarge(string heardText, TimeSpan playedDuration)
        {
            lock (_gate)
            {
                if (_barge is not null || _bargeWaitOver)
                {
                    return false;
                }

                _barge = (heardText, playedDuration);
            }

            _ = _bargeRecorded.TrySetResult();
            return true;
        }

        /// <summary>Marks the first text of a step handed to the output.</summary>
        public void MarkFirstText()
        {
            lock (_gate)
            {
                _beforeLastSpokenStep = _forwarded;
            }

            _forwardedText = true;
        }

        /// <summary>Keeps what one step forwarded, once its forwarding stopped.</summary>
        /// <param name="forwarded">What the step forwarded.</param>
        public void AddStep(TextForwardingResult forwarded)
        {
            lock (_gate)
            {
                _forwarded += forwarded.Text;
                _cutMidStep = forwarded.Playback == TextPlayback.Partial;
            }
        }

        /// <summary>Cuts the reply's engine turn to what the caller heard, without interrupting its speech.</summary>
        /// <param name="heardText">The text the caller heard, as the transport reported it.</param>
        /// <param name="playedDuration">How much of the reply played, as the transport reported it.</param>
        public void CutHeard(string heardText, TimeSpan playedDuration)
        {
            _ = stream.Cut(Heard(heardText, playedDuration));
        }

        /// <summary>Cuts the reply's engine turn to what reached the caller, as its interruption does.</summary>
        /// <returns><see langword="true"/> when the turn has started, so its end is worth waiting for.</returns>
        public bool CutInterrupted()
        {
            TurnCut cut;
            lock (_gate)
            {
                bool forwardedText = _forwardedText;
                _cutSettled = !forwardedText || _barge is not null;
                cut = !forwardedText
                    ? NothingHeard
                    : _barge is { } heard ? Heard(heard.HeardText, heard.PlayedDuration) : new TurnCut(_forwarded, Played: null);
            }

            return stream.Cut(cut);
        }

        /// <summary>
        /// After a final prompt cut a step mid-way, waits for the transport's report of what was heard, as LiveKit
        /// waits for playout before it keeps the synchronized transcript, and recuts the turn to it.
        /// </summary>
        /// <param name="cancellationToken">The speech's own token, which the interruption backstop cancels.</param>
        public async Task RecutToReportAsync(CancellationToken cancellationToken)
        {
            await WaitForBargeAsync(cancellationToken).ConfigureAwait(false);

            TurnCut cut;
            lock (_gate)
            {
                if (_cutSettled || _barge is not { } heard)
                {
                    return;
                }

                _cutSettled = true;
                cut = Heard(heard.HeardText, heard.PlayedDuration);
            }

            stream.Recut(cut);
        }

        /// <summary>Builds the cut for a transport report, which covers only the step whose text played last.</summary>
        private TurnCut Heard(string heardText, TimeSpan playedDuration)
        {
            lock (_gate)
            {
                return new TurnCut(_beforeLastSpokenStep + heardText, playedDuration);
            }
        }

        private async Task WaitForBargeAsync(CancellationToken cancellationToken)
        {
            bool wait;
            lock (_gate)
            {
                wait = _expectsBarge && _cutMidStep && _barge is null;
                _bargeWaitOver = !wait;
            }

            if (!wait)
            {
                return;
            }

            using CancellationTokenSource gone = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, transportToken);
            try
            {
                await _bargeRecorded.Task.WaitAsync(heardTextWait, time, gone.Token).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // No report: the transport had nothing more to say about this step.
            }
            catch (OperationCanceledException) when (gone.IsCancellationRequested)
            {
                // The transport went away or the backstop fired: no report can come now.
            }
            finally
            {
                lock (_gate)
                {
                    _bargeWaitOver = true;
                }
            }
        }
    }
}
