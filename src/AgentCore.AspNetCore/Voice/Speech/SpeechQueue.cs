// Portions derived from LiveKit Agents, livekit-agents/livekit/agents/voice/agent_activity.py,
// commit d8405f132e1bd960f298190c18daf81ffc1faf45. Copyright 2023 LiveKit, Inc.
// Licensed under the Apache License, Version 2.0. Modified: translated to C#.

using System.Diagnostics.CodeAnalysis;

namespace AgentCore.AspNetCore.Voice.Speech
{
    /// <summary>
    /// The speeches waiting to play: highest priority first, first queued first within a priority.
    /// </summary>
    internal sealed class SpeechQueue
    {
        private readonly PriorityQueue<SpeechHandle, (int NegPriority, long Seq)> _heap = new();

        private long _seq;

        /// <summary>Gets how many speeches are waiting.</summary>
        public int Count => _heap.Count;

        /// <summary>Queues a speech behind every one of the same or higher priority.</summary>
        /// <param name="speech">The speech to queue.</param>
        /// <param name="priority">Its priority.</param>
        public void Enqueue(SpeechHandle speech, SpeechPriority priority)
        {
            _heap.Enqueue(speech, (-(int)priority, _seq++));
        }

        /// <summary>Takes the speech that plays next.</summary>
        /// <param name="speech">The speech, when there was one.</param>
        /// <returns><see langword="false"/> when the queue is empty.</returns>
        public bool TryDequeue([MaybeNullWhen(false)] out SpeechHandle speech)
        {
            return _heap.TryDequeue(out speech, out _);
        }

        /// <summary>Lists the waiting speeches in the order they would play. The heap's own order is not that.</summary>
        public IReadOnlyList<SpeechHandle> SortedSnapshot()
        {
            return [.. _heap.UnorderedItems.OrderBy(static item => item.Priority).Select(static item => item.Element)];
        }
    }
}
