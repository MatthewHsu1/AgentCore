// Portions derived from LiveKit Agents, livekit-agents/livekit/agents/voice/generation.py (perform_llm_inference,
// ttft) and livekit-agents/livekit/agents/voice/agent_activity.py (_pipeline_reply_task_impl metrics, interrupt),
// commit d8405f132e1bd960f298190c18daf81ffc1faf45. Copyright 2023 LiveKit, Inc.
// Licensed under the Apache License, Version 2.0. Modified: translated to C#; the four readings are reported
// as TurnLatency notices under AgentCore's own metric names, and every audio-only reading is dropped.

using AgentCore.Application.Hooks.Notices;

namespace AgentCore.AspNetCore.Voice.Turns
{
    /// <summary>Times one pipeline reply at the points LiveKit measures it.</summary>
    /// <param name="time">The clock every reading is taken on.</param>
    /// <param name="userTurnEndedAt">
    /// The <see cref="TimeProvider.GetTimestamp"/> at which the caller's final words arrived, or
    /// <see langword="null"/> for a reply that answers no caller turn, which then reports no first-speech
    /// and no reply-end reading.
    /// </param>
    /// <param name="report">Where each reading goes, with the engine turn it belongs to when there is one.</param>
    internal sealed class TurnMetrics(TimeProvider time, long? userTurnEndedAt, Action<LatencyMetric, TimeSpan, int?> report)
    {
        private long _stepStartedAt;

        private bool _stepContentSeen = true;

        private int _firstSpeechMarked;

        private int _replyEndMarked;

        private long? _speechEndedAt;

        /// <summary>Gets or sets the engine turn this reply runs, once the engine has started it.</summary>
        public int? TurnIndex { get; set; }

        /// <summary>Reports how long an audio-activity interruption took to take effect.</summary>
        /// <param name="time">The clock <paramref name="detectedAt"/> was read on.</param>
        /// <param name="detectedAt">When the barge-in arrived.</param>
        /// <param name="report">Where the reading goes.</param>
        public static void RecordBargeIn(TimeProvider time, long detectedAt, Action<LatencyMetric, TimeSpan, int?> report)
        {
            report(LatencyMetric.BargeIn, time.GetElapsedTime(detectedAt), null);
        }

        /// <summary>Marks the start of one model step: the engine call for step 1, the end of the tool round before for any later step.</summary>
        public void StartStep()
        {
            _stepStartedAt = time.GetTimestamp();
            _stepContentSeen = false;
        }

        /// <summary>Marks the first text or tool call of the current step. Only the first per step reports a reading.</summary>
        public void MarkFirstContent()
        {
            if (_stepContentSeen)
            {
                return;
            }

            _stepContentSeen = true;
            report(LatencyMetric.TimeToFirstToken, time.GetElapsedTime(_stepStartedAt), TurnIndex);
        }

        /// <summary>Marks the first text of this speech handed to the output. Only the first call reports a reading.</summary>
        public void MarkFirstSpeech()
        {
            if (userTurnEndedAt is long anchor && Claim(ref _firstSpeechMarked))
            {
                report(LatencyMetric.TimeToFirstSpeech, time.GetElapsedTime(anchor), TurnIndex);
            }
        }

        /// <summary>Marks the end of one step's text forwarding: LiveKit's <c>stopped_speaking_at</c> of that step.</summary>
        public void MarkSpeechEnded()
        {
            _speechEndedAt = time.GetTimestamp();
        }

        /// <summary>Reports the reply end at the last <see cref="MarkSpeechEnded"/>, for a speech that ended with nothing interrupting it.</summary>
        public void EndReply()
        {
            if (userTurnEndedAt is long anchor && _speechEndedAt is long speechEndedAt && Claim(ref _replyEndMarked))
            {
                report(LatencyMetric.TimeToReplyEnd, time.GetElapsedTime(anchor, speechEndedAt), TurnIndex);
            }
        }

        private static bool Claim(ref int gate)
        {
            return Interlocked.Exchange(ref gate, 1) == 0;
        }
    }
}
