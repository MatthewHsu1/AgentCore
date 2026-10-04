// Portions derived from LiveKit Agents, livekit-agents/livekit/agents/voice/agent_activity.py:653-658
// (allow_interruptions), 1692-1766 (say), 1768-1866 (_generate_reply, LLM branch 1843-1865), 1935-1944
// (interrupt's done future), 2310-2375 (_interrupt_by_audio_activity, reduced), 2531-2556
// (on_final_transcript, its interrupt only),
// commit d8405f132e1bd960f298190c18daf81ffc1faf45. Copyright 2023 LiveKit, Inc.
// Licensed under the Apache License, Version 2.0. Modified: translated to C#; no realtime model, no tool
// resolution, no min-words gate, no false-interruption pause; the reply runs as one engine turn.

using AgentCore.Application.Hooks.Engine;
using AgentCore.Application.Hooks.Notices;
using AgentCore.Application.Ports;
using AgentCore.AspNetCore.Voice.Diagnostics;
using AgentCore.AspNetCore.Voice.Filler;
using AgentCore.AspNetCore.Voice.Options;
using AgentCore.AspNetCore.Voice.Session;
using AgentCore.AspNetCore.Voice.Speech;
using AgentCore.AspNetCore.Voice.Speech.Replies;

namespace AgentCore.AspNetCore.Voice.Turns
{
    /// <summary>Starts the agent's replies and routes a barge-in to the speech it cuts.</summary>
    internal sealed class VoiceActivity
    {
        private static readonly IReadOnlyDictionary<string, FillerOptions> NoFillers = new Dictionary<string, FillerOptions>();

        private readonly VoiceSession _session;

        private readonly bool? _allowInterruptions;

        private readonly CancellationToken _engineToken;

        private readonly IReadOnlyDictionary<string, FillerOptions> _fillers;

        private readonly TimeSpan _heardTextWait;

        private readonly Action<LatencyMetric, TimeSpan, int?>? _latency;

        private readonly Action<ReplyHearing>? _agentLine;

        private readonly List<PipelineReply> _replies = [];

        private readonly List<ReplyHearing> _openWindows = [];

        private IConversationPort _port;

        private PipelineReply? _lastHeard;

        private Task _engineRun = Task.CompletedTask;

        /// <param name="session">The session every speech plays in.</param>
        /// <param name="port">The conversation every reply's engine turn runs on.</param>
        /// <param name="engineToken">Cancels every engine turn. Cancelled once, when the transport goes away.</param>
        /// <param name="allowInterruptions">
        /// This agent's override of <see cref="VoiceSession.DefaultAllowInterruptions"/>, or
        /// <see langword="null"/> to keep the session's.
        /// </param>
        /// <param name="fillers">The filler each tool id opens while its call runs, or <see langword="null"/> for none.</param>
        /// <param name="heardTextWait">
        /// How long a reply a final prompt cut mid-step waits for the transport's report, or <see langword="null"/>
        /// for <see cref="VoiceOptions.DefaultHeardTextWait"/>.
        /// </param>
        /// <param name="latency">Where each latency reading goes, or <see langword="null"/> for a <see cref="TurnLatency"/> notice on the conversation's hooks.</param>
        /// <param name="agentLine">
        /// Takes each reply as it starts, in order. What the caller heard of it is known once
        /// <see cref="ReplyHearing.HeardWhenSettledAsync"/> completes.
        /// </param>
        public VoiceActivity(
            VoiceSession session,
            IConversationPort port,
            CancellationToken engineToken,
            bool? allowInterruptions = null,
            IReadOnlyDictionary<string, FillerOptions>? fillers = null,
            TimeSpan? heardTextWait = null,
            Action<LatencyMetric, TimeSpan, int?>? latency = null,
            Action<ReplyHearing>? agentLine = null)
        {
            _session = session;
            _port = port;
            _engineToken = engineToken;
            _allowInterruptions = allowInterruptions;
            _fillers = fillers ?? NoFillers;
            _heardTextWait = heardTextWait ?? VoiceOptions.DefaultHeardTextWait;
            _latency = latency;
            _agentLine = agentLine;
            VoiceNotices.Attach(session, () => VoiceNotices.HooksOf(CurrentPort()));
        }

        /// <summary>Gets whether a speech this activity starts may be interrupted, unless the call says otherwise.</summary>
        public bool AllowInterruptions => _allowInterruptions ?? VoiceSession.DefaultAllowInterruptions;

        /// <summary>Gets a task that completes once the last engine turn this activity started has ended. It never faults.</summary>
        public Task EngineRun
        {
            get
            {
                lock (_session.SessionLock)
                {
                    return _engineRun;
                }
            }
        }

        /// <summary>Points the replies started from now on at another conversation. A reply already started keeps its own.</summary>
        /// <param name="port">The conversation every later reply's engine turn runs on.</param>
        public void Rebind(IConversationPort port)
        {
            lock (_session.SessionLock)
            {
                _port = port;
            }
        }

        /// <summary>Speaks fixed text through <see cref="VoiceSession.Say"/>.</summary>
        /// <param name="text">The text to speak. Never empty.</param>
        /// <param name="allowInterruptions">Overrides <see cref="AllowInterruptions"/> for this speech.</param>
        /// <returns>A handle to the speech.</returns>
        public SpeechHandle Say(string text, bool? allowInterruptions = null)
        {
            return _session.Say(text, allowInterruptions ?? AllowInterruptions);
        }

        /// <summary>Starts a reply to <paramref name="userInput"/> and queues its speech.</summary>
        /// <param name="userInput">What the caller said.</param>
        /// <param name="allowInterruptions">Overrides <see cref="AllowInterruptions"/> for this speech.</param>
        /// <param name="userTurnEndedAt">
        /// The <see cref="TimeProvider.GetTimestamp"/> at which the caller's final words arrived, or
        /// <see langword="null"/> when the reply answers no caller turn.
        /// </param>
        /// <returns>A handle to the speech, done once every step was spoken or the speech was interrupted.</returns>
        /// <exception cref="InvalidOperationException">Scheduling is paused; no engine turn is started.</exception>
        public SpeechHandle GenerateReply(string userInput, bool? allowInterruptions = null, long? userTurnEndedAt = null)
        {
            return TryGenerateReply(userInput, allowInterruptions, userTurnEndedAt)
                ?? throw new InvalidOperationException(
                    "cannot schedule new speech, the speech scheduling is draining/pausing, the speech will be cancelled");
        }

        /// <summary>Starts a reply to <paramref name="userInput"/> and queues its speech, unless scheduling is paused.</summary>
        /// <param name="userInput">What the caller said.</param>
        /// <param name="allowInterruptions">Overrides <see cref="AllowInterruptions"/> for this speech.</param>
        /// <param name="userTurnEndedAt">As <see cref="GenerateReply"/> takes it.</param>
        /// <returns>
        /// A handle to the speech, or <see langword="null"/> when scheduling is paused: then no engine turn starts,
        /// as LiveKit skips the reply once the session is closing (<c>agent_activity.py:2788-2796</c>).
        /// </returns>
        public SpeechHandle? TryGenerateReply(string userInput, bool? allowInterruptions = null, long? userTurnEndedAt = null)
        {
            SpeechHandle handle = SpeechHandle.Create(_session.Time, _session.Logger, allowInterruptions ?? AllowInterruptions);

            // One hold of the session lock from the paused check to the queueing, so a close that pauses
            // scheduling lands either before the engine turn starts or after the speech is queued.
            lock (_session.SessionLock)
            {
                if (_session.Scheduler.SchedulingPaused)
                {
                    return null;
                }

                TurnMetrics metrics = new(_session.Time, userTurnEndedAt, LatencySink(VoiceNotices.HooksOf(_port)));
                EngineReplyStream stream = EngineReplyStream.Start(
                    _port, userInput, _engineRun, handle, metrics, _session.Logger, _engineToken);
                _engineRun = stream.Completion;
                PipelineReply reply = new(handle, _session, stream, metrics, _fillers, OnFirstText, _heardTextWait, _engineToken);
                _replies.Add(reply);
                _openWindows.Add(reply.Hearing);
                _agentLine?.Invoke(reply.Hearing);

                _ = _session.Scheduler.CreateSpeechTask(reply.RunAsync, handle);

                // On the handle, not the task, as VoiceSession.Say does: it runs once the speech is marked done.
                handle.AddDoneCallback(done =>
                {
                    lock (_session.SessionLock)
                    {
                        _ = _replies.Remove(reply);
                    }

                    reply.Hearing.MarkSpoken(done.IsInterrupted);
                    _session.OnSpeechDone();
                });

                _session.Scheduler.ScheduleSpeech(handle, SpeechPriority.Normal);
            }

            return handle;
        }

        /// <summary>
        /// Interrupts the current speech for a final prompt, before the caller's silence can release a step it holds, and
        /// lets every earlier reply's barge window close once the report that may follow the prompt had its wait.
        /// </summary>
        public void InterruptByFinalTranscript()
        {
            List<ReplyHearing> closing;
            lock (_session.SessionLock)
            {
                // Kept until closed, so the drain still closes a window that waits for its report.
                _ = _openWindows.RemoveAll(hearing => hearing.BargeWindowClosed);
                closing = [.. _openWindows];

                if (_session.Scheduler.CurrentSpeech is { IsInterrupted: false, AllowInterruptions: true } current)
                {
                    _replies.Find(reply => reply.SpeechHandle == current)?.Hearing.ExpectBarge();
                    _ = current.Interrupt(source: InterruptionSource.AudioActivity);
                }
            }

            foreach (ReplyHearing hearing in closing)
            {
                _ = hearing.CloseBargeWindowAfterReportWaitAsync();
            }
        }

        /// <summary>Closes every reply's barge window at once: the transport is gone, and no report can come.</summary>
        public void CloseBargeWindows()
        {
            List<ReplyHearing> closing;
            lock (_session.SessionLock)
            {
                closing = [.. _openWindows];
                _openWindows.Clear();
            }

            foreach (ReplyHearing hearing in closing)
            {
                hearing.CloseBargeWindow();
            }
        }

        /// <summary>Interrupts the speech a barge-in cut, and hands the transport's report to the turn the caller heard.</summary>
        /// <param name="heardText">The text the caller heard, as the transport reported it.</param>
        /// <param name="playedDuration">How much of the reply played, as the transport reported it.</param>
        /// <returns>A task that completes once the interrupted speech is done and the latency is recorded.</returns>
        public Task InterruptByAudioActivity(string heardText, TimeSpan playedDuration)
        {
            long detectedAt = _session.Time.GetTimestamp();
            SpeechHandle? interrupted;
            PipelineReply? cutReply = null;
            PipelineReply? heardEarlier = null;
            lock (_session.SessionLock)
            {
                interrupted = BargeTargetLocked();
                if (interrupted is null && _replies.FindLast(reply => reply.Hearing.AwaitsBarge) is { } awaiting)
                {
                    // A final prompt already interrupted it, and it waits for this report to settle its turn's text.
                    return awaiting.Hearing.RecordBarge(heardText, playedDuration)
                        ? RecordBargeInWhenDone(awaiting.SpeechHandle, detectedAt)
                        : Task.CompletedTask;
                }

                if (interrupted is null)
                {
                    heardEarlier = _lastHeard is { SpeechHandle.IsDone: true } last ? last : null;
                }
                else
                {
                    cutReply = _replies.Find(reply => reply.SpeechHandle == interrupted);
                    _ = cutReply?.Hearing.RecordBarge(heardText, playedDuration);
                    _ = interrupted.Interrupt(source: InterruptionSource.AudioActivity);
                }
            }

            // Before returning, so the engine holds the cut before the transport's next frame is read. The speech's
            // own teardown runs later on another thread, and a turn that finished first would commit unheard words.
            if (interrupted is null)
            {
                if (heardEarlier is not null && !heardEarlier.Hearing.CutHeard(heardText, playedDuration))
                {
                    VoiceConversationLog.BargeReportTooLate(_session.Logger);
                }

                return Task.CompletedTask;
            }

            _ = cutReply?.Hearing.CutInterrupted();

            return RecordBargeInWhenDone(interrupted, detectedAt);
        }

        /// <summary>Records the barge-in's latency once the speech it interrupted is done, as LiveKit's <c>interrupt()</c> future resolves.</summary>
        /// <returns>A task that completes once the latency is recorded.</returns>
        private Task RecordBargeInWhenDone(SpeechHandle interrupted, long detectedAt)
        {
            TaskCompletionSource recorded = new(TaskCreationOptions.RunContinuationsAsynchronously);
            interrupted.AddDoneCallback(done =>
            {
                TurnMetrics.RecordBargeIn(_session.Time, detectedAt, LatencySink(VoiceNotices.HooksOf(CurrentPort())));
                _ = recorded.TrySetResult();
            });
            return recorded.Task;
        }

        private IConversationPort CurrentPort()
        {
            lock (_session.SessionLock)
            {
                return _port;
            }
        }

        // A reply keeps the hooks of the conversation it started in, so a rebind does not move its readings.
        private Action<LatencyMetric, TimeSpan, int?> LatencySink(SessionHooks? hooks)
        {
            return _latency ?? ((metric, value, turnIndex) =>
            {
                if (hooks is not null && hooks.Wants<TurnLatency>())
                {
                    _ = hooks.Raise(new TurnLatency(hooks.Scope(turnIndex, stage: null), metric, value));
                }
            });
        }

        private void OnFirstText(PipelineReply reply)
        {
            lock (_session.SessionLock)
            {
                _lastHeard = reply;
            }
        }

        private SpeechHandle? BargeTargetLocked()
        {
            SpeechHandle? current = _session.Scheduler.CurrentSpeech;
            PipelineReply? currentReply = current is null ? null : _replies.Find(reply => reply.SpeechHandle == current);
            if (current is not null && (currentReply is null || currentReply.Hearing.HasForwardedText))
            {
                return !current.IsInterrupted && current.AllowInterruptions ? current : null;
            }

            PipelineReply? hearing = _replies.FindLast(reply => reply.Hearing.HasForwardedText);
            return hearing?.SpeechHandle is { IsDone: false, IsInterrupted: false, AllowInterruptions: true } speech
                ? speech
                : null;
        }
    }
}
