// Portions derived from LiveKit Agents, livekit-agents/livekit/agents/voice/speech_handle.py and
// livekit-agents/livekit/agents/voice/agent_activity.py,
// commit d8405f132e1bd960f298190c18daf81ffc1faf45. Copyright 2023 LiveKit, Inc.
// Licensed under the Apache License, Version 2.0. Modified: translated to C#.

using System.Diagnostics.CodeAnalysis;

namespace AgentCore.AspNetCore.Voice.Speech
{
    /// <summary>
    /// The tasks one <see cref="SpeechHandle"/> runs, the token they honour, and the interruption backstop
    /// that cancels them.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1001:Types that own disposable fields should be disposable",
        Justification = "Tasks keep the token past the speech's end, and a source with no timer and no wait handle holds nothing to release.")]
    internal sealed class SpeechTasks
    {
        private readonly Lock _gate = new();

        private readonly List<Task> _tasks = [];

        private readonly CancellationTokenSource _cancellation = new();

        private ITimer? _backstop;

        private Action? _onBackstop;

        /// <summary>Gets the token every task of the speech honours.</summary>
        public CancellationToken Token => _cancellation.Token;

        /// <summary>Gets whether every task added so far has finished.</summary>
        public bool AllDone
        {
            get
            {
                lock (_gate)
                {
                    return _tasks.TrueForAll(static task => task.IsCompleted);
                }
            }
        }

        /// <summary>Links a task to the speech.</summary>
        /// <param name="task">A task started for the speech.</param>
        public void Add(Task task)
        {
            lock (_gate)
            {
                _tasks.Add(task);
            }
        }

        /// <summary>Cancels <see cref="Token"/>. Registered callbacks run on the caller's thread, so no lock may be held.</summary>
        public void Cancel()
        {
            _cancellation.Cancel();
        }

        /// <summary>Arms the backstop, once.</summary>
        /// <param name="time">The clock the timer runs on.</param>
        /// <param name="dueTime">How long until it fires.</param>
        /// <param name="onBackstop">Runs on the timer thread, outside this lock, unless <see cref="DisarmBackstop"/> ran first.</param>
        public void ArmBackstop(TimeProvider time, TimeSpan dueTime, Action onBackstop)
        {
            lock (_gate)
            {
                _onBackstop = onBackstop;
                _backstop = time.CreateTimer(
                    static state => ((SpeechTasks)state!).OnBackstop(), this, dueTime, Timeout.InfiniteTimeSpan);
            }
        }

        /// <summary>Disarms the backstop, if armed. A callback already dispatched then does nothing.</summary>
        public void DisarmBackstop()
        {
            _ = TakeBackstop();
        }

        private void OnBackstop()
        {
            // A timer callback already dispatched can still run after DisarmBackstop disposed the timer.
            TakeBackstop()?.Invoke();
        }

        private Action? TakeBackstop()
        {
            ITimer? backstop;
            Action? onBackstop;
            lock (_gate)
            {
                backstop = _backstop;
                onBackstop = _onBackstop;
                _backstop = null;
                _onBackstop = null;
            }

            backstop?.Dispose();
            return backstop is null ? null : onBackstop;
        }
    }
}
