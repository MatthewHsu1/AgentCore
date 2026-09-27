// Portions derived from LiveKit Agents, livekit-agents/livekit/agents/voice/agent_activity.py
// (_background_speeches, _interrupt_background_speeches), commit
// d8405f132e1bd960f298190c18daf81ffc1faf45. Copyright 2023 LiveKit, Inc.
// Licensed under the Apache License, Version 2.0. Modified: translated to C#.

namespace AgentCore.AspNetCore.Voice
{
    /// <summary>The speeches that finished speaking but still wait on their own tools (plan 2.1).</summary>
    /// <remarks>Not thread-safe. <see cref="SpeechScheduler"/> touches it only under the session lock.</remarks>
    internal sealed class BackgroundSpeeches
    {
        private readonly HashSet<SpeechHandle> _speeches = [];

        /// <summary>Gets whether any speech is waiting on its own tools.</summary>
        public bool Any => _speeches.Count > 0;

        /// <summary>Marks a speech as waiting for its tools after its words were spoken.</summary>
        /// <param name="speech">The speech.</param>
        public void Add(SpeechHandle speech)
        {
            _ = _speeches.Add(speech);
        }

        /// <summary>Removes a speech <see cref="Add"/> added, once its tools finished.</summary>
        /// <param name="speech">The speech.</param>
        public void Remove(SpeechHandle speech)
        {
            _ = _speeches.Remove(speech);
        }

        /// <summary>Interrupts every speech that allows it, or all of them with <paramref name="force"/>.</summary>
        /// <param name="force">Interrupt even speeches that disallow interruptions.</param>
        /// <param name="source">Why.</param>
        /// <returns>The speeches interrupted.</returns>
        public List<SpeechHandle> InterruptAll(bool force, InterruptionSource source)
        {
            List<SpeechHandle> interrupted = [];
            foreach (SpeechHandle speech in _speeches)
            {
                if (force || speech.AllowInterruptions)
                {
                    interrupted.Add(speech.Interrupt(force, source));
                }
            }

            return interrupted;
        }
    }
}
