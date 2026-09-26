// Portions derived from LiveKit Agents, livekit-agents/livekit/agents/voice/filler_scheduler.py:16
// (_FillerSource), commit d8405f132e1bd960f298190c18daf81ffc1faf45. Copyright 2023 LiveKit, Inc.
// Licensed under the Apache License, Version 2.0. Modified: translated to C#.

namespace AgentCore.AspNetCore.Voice
{
    /// <summary>What a filler says: fixed text, or a callable invoked lazily at fire time with the fire count so far.</summary>
    internal readonly struct FillerSource
    {
        private readonly string? _text;

        private readonly Func<int, object?>? _factory;

        private FillerSource(string? text, Func<int, object?>? factory)
        {
            _text = text;
            _factory = factory;
        }

        public static implicit operator FillerSource(string text)
        {
            return new(text, null);
        }

        /// <summary>Creates a source invoked lazily at fire time.</summary>
        /// <param name="factory">Returns a <see cref="string"/>, a <see cref="SpeechHandle"/>, or <see langword="null"/>.</param>
        public static FillerSource FromCallable(Func<int, object?> factory)
        {
            return new(null, factory);
        }

        /// <summary>Resolves what to fire, at the given fire count.</summary>
        internal object? Resolve(int step)
        {
            return _factory is not null ? _factory(step) : _text;
        }
    }
}
