// Portions derived from LiveKit Agents, livekit-agents/livekit/agents/voice/agent_activity.py:3833-3863
// (_pipeline_reply_task_impl, the interrupted reply's forwarded_text) and generation.py:737-768 (forward_generation,
// the interrupted branch's wait for playout), commit d8405f132e1bd960f298190c18daf81ffc1faf45.
// Copyright 2023 LiveKit, Inc. Licensed under the Apache License, Version 2.0. Modified: translated to C#; the
// transport, not an audio sink, reports what played, and the reply's engine turn is cut rather than a chat
// context written.

using AgentCore.Application.Runtime.Cut;

namespace AgentCore.AspNetCore.Voice.Speech.Replies
{
    /// <summary>What the caller heard of one pipeline reply, and the cut its engine turn takes from it.</summary>
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

        private readonly TaskCompletionSource _spoken = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _bargeWindowClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private string _forwarded = string.Empty;

        private string _beforeLastSpokenStep = string.Empty;

        private string? _cutText;

        private (string HeardText, TimeSpan PlayedDuration)? _barge;

        private volatile bool _forwardedText;

        private bool _cutMidStep;

        private bool _expectsBarge;

        private bool _bargeWaitOver;

        private bool _cutSettled;

        private bool _spokeWhole;

        private int _reportWaitStarted;

        /// <summary>Gets whether the reply has handed any text to the output yet.</summary>
        public bool HasForwardedText => _forwardedText;

        /// <summary>Gets what the caller heard of the reply: the text its turn was last cut to, or everything forwarded when nothing cut it.</summary>
        public string HeardText
        {
            get
            {
                lock (_gate)
                {
                    return _cutText ?? _forwarded;
                }
            }
        }

        /// <summary>Gets whether the window in which a barge-in report can still cut the reply has closed.</summary>
        public bool BargeWindowClosed => _bargeWindowClosed.Task.IsCompleted;

        /// <summary>Gets the index of the reply's engine turn, or <see langword="null"/> while it has not started.</summary>
        public int? TurnIndex => stream.TurnIndex;

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

        /// <summary>
        /// Marks the interruption about to land as a final prompt's, which the transport's report may follow. A reply
        /// spoken whole loses nothing to it, so it waits for no report on that account.
        /// </summary>
        public void ExpectBarge()
        {
            lock (_gate)
            {
                _expectsBarge = !_spokeWhole;
            }
        }

        /// <summary>
        /// Marks every word of the reply forwarded and its engine turn over. An interruption from here on cuts nothing,
        /// so the transport's report settles the line as it does for a reply already done.
        /// </summary>
        public void MarkSpokenWhole()
        {
            (string HeardText, TimeSpan PlayedDuration)? kept = null;
            lock (_gate)
            {
                _spokeWhole = true;

                // A final prompt's interruption landed after the last word and kept a report for a recut that will not run.
                if (_expectsBarge && _cutText is null)
                {
                    kept = _barge;
                    _barge = null;
                }

                _expectsBarge = false;
            }

            if (kept is { } heard)
            {
                _ = CutHeard(heard.HeardText, heard.PlayedDuration);
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
                if (!TryKeepBargeLocked(heardText, playedDuration))
                {
                    return false;
                }
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

        /// <summary>
        /// Cuts the reply's engine turn to what the caller heard, without interrupting its speech, and closes its barge
        /// window. A report that comes once the window closed, or after the first, changes nothing.
        /// </summary>
        /// <param name="heardText">The text the caller heard, as the transport reported it.</param>
        /// <param name="playedDuration">How much of the reply played, as the transport reported it.</param>
        /// <returns><see langword="true"/> when the report was kept; <see langword="false"/> when it came too late.</returns>
        public bool CutHeard(string heardText, TimeSpan playedDuration)
        {
            TurnCut cut;
            lock (_gate)
            {
                // Kept with the report, so a window that closes right after it still gives the cut text, not all forwarded.
                if (!TryKeepBargeLocked(heardText, playedDuration))
                {
                    return false;
                }

                cut = Heard(heardText, playedDuration);
                _cutText = cut.ShownText;
            }

            _ = _bargeRecorded.TrySetResult();
            _ = stream.Cut(cut);
            CloseBargeWindow();
            return true;
        }

        /// <summary>
        /// Marks the reply's speech done: its text is with the transport, which may still be playing it. An interrupted
        /// reply settled its cut before its speech was done, so its barge window closes too, unless it was spoken whole
        /// and nothing cut it.
        /// </summary>
        /// <param name="interrupted">Whether the speech was interrupted.</param>
        public void MarkSpoken(bool interrupted)
        {
            bool settled;
            lock (_gate)
            {
                settled = interrupted && (!_spokeWhole || _cutText is not null);
            }

            _ = _spoken.TrySetResult();
            if (settled)
            {
                CloseBargeWindow();
            }
        }

        /// <summary>Closes the window in which a barge-in report can still cut the reply: no report for it can count now.</summary>
        public void CloseBargeWindow()
        {
            lock (_gate)
            {
                _bargeWaitOver = true;
            }

            _ = _bargeWindowClosed.TrySetResult();
        }

        /// <summary>
        /// The caller spoke again. Once the speech is done, the transport's report of what was heard may still follow the
        /// final prompt, so the window stays open for the same wait a reply a final prompt cut mid-step gives it.
        /// </summary>
        /// <returns>A task that completes once the window closed, or at once when an earlier call already waits. It never faults.</returns>
        public async Task CloseBargeWindowAfterReportWaitAsync()
        {
            if (Interlocked.Exchange(ref _reportWaitStarted, 1) == 1)
            {
                return;
            }

            await _spoken.Task.ConfigureAwait(false);
            if (_bargeWindowClosed.Task.IsCompleted)
            {
                return;
            }

            await WaitForReportAsync(CancellationToken.None).ConfigureAwait(false);
            CloseBargeWindow();
        }

        /// <summary>Waits until the speech is done and its barge window closed, then gives what the caller heard.</summary>
        /// <returns><see cref="HeardText"/>, once nothing can change it; empty when the caller heard nothing.</returns>
        public async Task<string> HeardWhenSettledAsync()
        {
            await Task.WhenAll(_spoken.Task, _bargeWindowClosed.Task).ConfigureAwait(false);

            return HeardText;
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

                _cutText = cut.ShownText;
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

                _cutText = cut.ShownText;
            }

            stream.Recut(cut);
        }

        private bool TryKeepBargeLocked(string heardText, TimeSpan playedDuration)
        {
            if (_barge is not null || _bargeWaitOver)
            {
                return false;
            }

            _barge = (heardText, playedDuration);
            return true;
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

            await WaitForReportAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task WaitForReportAsync(CancellationToken cancellationToken)
        {
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
