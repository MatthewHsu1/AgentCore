// Portions derived from LiveKit Agents, livekit-agents/livekit/agents/voice/speech_handle.py,
// commit d8405f132e1bd960f298190c18daf81ffc1faf45. Copyright 2023 LiveKit, Inc.
// Licensed under the Apache License, Version 2.0. Modified: translated to C#.

using Microsoft.Extensions.Logging;

namespace AgentCore.AspNetCore.Voice
{
    /// <summary>The callbacks waiting for one <see cref="SpeechHandle"/> to be done.</summary>
    /// <remarks>
    /// Every callback runs on the thread pool, never on the thread that marked the speech done, as asyncio's
    /// <c>call_soon</c> runs it on a later loop turn. That thread may hold the session lock.
    /// </remarks>
    /// <param name="owner">The speech every callback is handed.</param>
    /// <param name="logger">Where a throwing callback is reported.</param>
    internal sealed class SpeechDoneCallbacks(SpeechHandle owner, ILogger logger)
    {
        private readonly Lock _gate = new();

        private readonly List<Action<SpeechHandle>> _callbacks = [];

        private bool _fired;

        /// <summary>Registers a callback, or queues it at once if the speech is already done.</summary>
        /// <param name="callback">The callback. Registering it twice keeps one.</param>
        public void Add(Action<SpeechHandle> callback)
        {
            lock (_gate)
            {
                if (!_fired)
                {
                    if (!_callbacks.Contains(callback))
                    {
                        _callbacks.Add(callback);
                    }

                    return;
                }
            }

            _ = ThreadPool.UnsafeQueueUserWorkItem(
                static state => state.Self.Invoke(state.Callback), (Self: this, Callback: callback), preferLocal: false);
        }

        /// <summary>Removes a registered callback, if it has not run yet.</summary>
        /// <param name="callback">The callback.</param>
        public void Remove(Action<SpeechHandle> callback)
        {
            lock (_gate)
            {
                _ = _callbacks.Remove(callback);
            }
        }

        /// <summary>Queues every registered callback, once. Later calls do nothing.</summary>
        public void Fire()
        {
            lock (_gate)
            {
                if (_fired)
                {
                    return;
                }

                _fired = true;
            }

            _ = ThreadPool.UnsafeQueueUserWorkItem(static self => self.RunAll(), this, preferLocal: false);
        }

        private void RunAll()
        {
            Action<SpeechHandle>[] callbacks;
            lock (_gate)
            {
                callbacks = [.. _callbacks];
            }

            foreach (Action<SpeechHandle> callback in callbacks)
            {
                Invoke(callback);
            }
        }

        private void Invoke(Action<SpeechHandle> callback)
        {
            try
            {
                callback(owner);
            }
            catch (Exception ex)
            {
                VoiceConversationLog.SpeechDoneCallbackFaulted(logger, owner.Id, ex);
            }
        }
    }
}
