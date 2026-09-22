using AgentCore.Application.Diagnostics;

namespace AgentCore.AspNetCore.Conversation
{
    /// <summary>
    /// Times one turn, anchored at the moment the transport handed over the caller's words.
    /// </summary>
    /// <param name="time">The clock the host bound. Reading it here is what lets a test own these numbers.</param>
    internal sealed class ConversationTurnClock(TimeProvider time)
    {
        private readonly TimeProvider _time = time;

        private readonly long _heard = time.GetTimestamp();

        private int _firstTokenMarked;

        private int _firstSpeechMarked;

        private int _replyEndMarked;

        /// <summary>Marks the first update the model produced for this turn.</summary>
        internal void MarkFirstToken()
        {
            if (Claim(ref _firstTokenMarked))
            {
                AgentCoreTelemetry.RecordTimeToFirstToken(Elapsed());
            }
        }

        /// <summary>Marks the first fragment of this turn handed to the output port.</summary>
        internal void MarkFirstSpeech()
        {
            if (Claim(ref _firstSpeechMarked))
            {
                AgentCoreTelemetry.RecordTimeToFirstSpeech(Elapsed());
            }
        }

        /// <summary>Marks the close of a reply that ended on its own.</summary>
        internal void MarkReplyEnd()
        {
            if (Claim(ref _replyEndMarked))
            {
                AgentCoreTelemetry.RecordTimeToReplyEnd(Elapsed());
            }
        }

        private TimeSpan Elapsed()
        {
            return _time.GetElapsedTime(_heard);
        }

        private static bool Claim(ref int gate)
        {
            return Interlocked.Exchange(ref gate, 1) == 0;
        }
    }
}
