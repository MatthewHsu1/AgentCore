// Portions derived from LiveKit Agents, livekit-agents/livekit/agents/voice/speech_handle.py,
// commit d8405f132e1bd960f298190c18daf81ffc1faf45. Copyright 2023 LiveKit, Inc.
// Licensed under the Apache License, Version 2.0. Modified: translated to C#; audio paths removed.

using AgentCore.AspNetCore.Voice.Diagnostics;
using AgentCore.AspNetCore.Voice.Threading;
using AgentCore.AspNetCore.Voice.Turns;
using Microsoft.Extensions.Logging;

namespace AgentCore.AspNetCore.Voice.Speech
{
    /// <summary>
    /// One speech the agent means to say, a reply or a <c>say</c>: whether it may be interrupted, whether it
    /// was, and when it is done.
    /// </summary>
    internal sealed class SpeechHandle
    {
        /// <summary>How long an interrupted speech may take to finish before it is cancelled and marked done.</summary>
        public static readonly TimeSpan InterruptionTimeout = TimeSpan.FromSeconds(5);

        private readonly Lock _gate = new();

        private readonly TimeProvider _time;

        private readonly ILogger _logger;

        private readonly TaskCompletionSource _interrupted = NewSource();

        private readonly TaskCompletionSource<SpeechHandle> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _scheduled = NewSource();

        private readonly AsyncEvent _authorize = new();

        private readonly List<TaskCompletionSource> _generations = [];

        private readonly SpeechTasks _tasks = new();

        private readonly SpeechDoneCallbacks _doneCallbacks;

        private bool _allowInterruptions;

        private int _numSteps = 1;

        private InterruptionSource? _interruptSource;

        private Exception? _error;

        private SpeechHandle(string id, bool allowInterruptions, TimeProvider time, ILogger logger)
        {
            Id = id;
            _allowInterruptions = allowInterruptions;
            _time = time;
            _logger = logger;
            _doneCallbacks = new SpeechDoneCallbacks(this, logger);
        }

        /// <summary>Gets the id of this speech, <c>speech_</c> and 12 hex digits.</summary>
        public string Id { get; }

        /// <summary>Gets how many model steps this speech has taken, starting at 1.</summary>
        public int NumSteps => Volatile.Read(ref _numSteps);

        /// <summary>Gets whether the scheduler has queued this speech.</summary>
        public bool IsScheduled => _scheduled.Task.IsCompleted;

        /// <summary>Gets whether this speech was interrupted.</summary>
        public bool IsInterrupted => _interrupted.Task.IsCompleted;

        /// <summary>Gets whether this speech is done, interrupted or not.</summary>
        public bool IsDone => _done.Task.IsCompleted;

        /// <summary>Gets the cause of the first interruption, or <see langword="null"/> when there was none.</summary>
        public InterruptionSource? InterruptSource
        {
            get
            {
                lock (_gate)
                {
                    return _interruptSource;
                }
            }
        }

        /// <summary>Gets or sets whether an interruption without <c>force</c> may cut this speech.</summary>
        /// <exception cref="InvalidOperationException">Set to <see langword="false"/> on an interrupted speech.</exception>
        public bool AllowInterruptions
        {
            get
            {
                lock (_gate)
                {
                    return _allowInterruptions;
                }
            }

            set
            {
                lock (_gate)
                {
                    if (IsInterrupted && !value)
                    {
                        throw new InvalidOperationException(
                            "Cannot set allow_interruptions to False, the SpeechHandle is already interrupted");
                    }

                    _allowInterruptions = value;
                }
            }
        }

        /// <summary>Gets the error the speech failed with, or <see langword="null"/>. Awaiting the handle never raises it.</summary>
        /// <exception cref="InvalidOperationException">The speech is not done yet.</exception>
        public Exception? Error
        {
            get
            {
                lock (_gate)
                {
                    return IsDone ? _error : throw new InvalidOperationException("SpeechHandle is not done yet");
                }
            }
        }

        /// <summary>Gets the token the speech's own tasks honour. Only the interruption backstop cancels it.</summary>
        public CancellationToken TaskCancellationToken => _tasks.Token;

        /// <summary>Creates a speech with a fresh id.</summary>
        /// <param name="time">The clock the interruption backstop and the queue wait run on.</param>
        /// <param name="logger">Where the backstop and a throwing done callback are reported.</param>
        /// <param name="allowInterruptions">Whether an interruption without <c>force</c> may cut it.</param>
        public static SpeechHandle Create(TimeProvider time, ILogger logger, bool allowInterruptions = true)
        {
            return new SpeechHandle("speech_" + Guid.NewGuid().ToString("N")[..12], allowInterruptions, time, logger);
        }

        /// <summary>Lets an awaiter wait for the speech to be done. It never raises <see cref="Error"/>.</summary>
        public System.Runtime.CompilerServices.TaskAwaiter<SpeechHandle> GetAwaiter()
        {
            return _done.Task.GetAwaiter();
        }

        /// <summary>Interrupts the speech.</summary>
        /// <param name="force">Interrupt even when the speech disallows interruptions.</param>
        /// <param name="source">Why. Only the first interruption's source is kept.</param>
        /// <returns>This handle.</returns>
        /// <exception cref="InvalidOperationException">The speech is running and disallows interruptions.</exception>
        public SpeechHandle Interrupt(bool force = false, InterruptionSource source = InterruptionSource.Programmatic)
        {
            lock (_gate)
            {
                if (IsInterrupted || IsDone)
                {
                    return this;
                }

                if (!force && !_allowInterruptions)
                {
                    throw new InvalidOperationException("This generation handle does not allow interruptions");
                }

                _interruptSource = source;
                CancelLocked();
                return this;
            }
        }

        /// <summary>Waits until the speech is done. It never raises <see cref="Error"/>.</summary>
        /// <param name="cancellationToken">Cancels this wait only, never the speech.</param>
        public Task WaitForPlayoutAsync(CancellationToken cancellationToken = default)
        {
            return _done.Task.WaitAsync(cancellationToken);
        }

        /// <summary>Calls <paramref name="callback"/> on the thread pool once the speech is done, or soon if it is already.</summary>
        /// <param name="callback">Runs outside every lock. A throw is logged, not raised.</param>
        public void AddDoneCallback(Action<SpeechHandle> callback)
        {
            _doneCallbacks.Add(callback);
        }

        /// <summary>Removes a callback <see cref="AddDoneCallback"/> registered, if it has not run yet.</summary>
        /// <param name="callback">The callback to remove.</param>
        public void RemoveDoneCallback(Action<SpeechHandle> callback)
        {
            _doneCallbacks.Remove(callback);
        }

        /// <summary>Waits for every task in <paramref name="tasks"/>, or for an interruption, whichever comes first.</summary>
        /// <param name="tasks">Waited on, never cancelled. A fault in one is not raised here.</param>
        public async Task WaitIfNotInterruptedAsync(IEnumerable<Task> tasks)
        {
            Task all = Task.WhenAll(tasks).ContinueWith(
                static finished => _ = finished.Exception,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            _ = await Task.WhenAny(all, _interrupted.Task).ConfigureAwait(false);
        }

        /// <summary>Adds one to <see cref="NumSteps"/>, when a tool round schedules the next model step.</summary>
        internal void IncrementNumSteps()
        {
            _ = Interlocked.Increment(ref _numSteps);
        }

        /// <summary>Links a task to this speech. The backstop cancels it; its owner marks the speech done once all are.</summary>
        /// <param name="task">A task started for this speech.</param>
        internal void AddTask(Task task)
        {
            _tasks.Add(task);
        }

        /// <summary>Marks the speech done if every task linked to it has finished.</summary>
        internal void MarkDoneIfTasksDone()
        {
            if (_tasks.AllDone)
            {
                MarkDone();
            }
        }

        /// <summary>
        /// Opens a new generation and the authorization gate, unless the speech is already done.
        /// </summary>
        /// <returns><see langword="false"/> when the speech is done, so no generation was opened.</returns>
        internal bool TryAuthorizeGeneration()
        {
            lock (_gate)
            {
                if (IsDone)
                {
                    return false;
                }

                _generations.Add(NewSource());
                _authorize.Set();
                return true;
            }
        }

        /// <summary>Closes the authorization gate once the speech's task has passed it.</summary>
        internal void ClearAuthorization()
        {
            _authorize.Clear();
        }

        /// <summary>Waits until the scheduler authorizes a generation.</summary>
        /// <param name="cancellationToken">Cancels this wait only.</param>
        internal Task WaitForAuthorizationAsync(CancellationToken cancellationToken = default)
        {
            return _authorize.WaitAsync(cancellationToken);
        }

        /// <summary>Gets whether any generation was ever authorized.</summary>
        internal bool HasGenerations
        {
            get
            {
                lock (_gate)
                {
                    return _generations.Count > 0;
                }
            }
        }

        /// <summary>Waits until a generation is marked done.</summary>
        /// <param name="stepIndex">Which generation, counted from the end when negative, as a Python index.</param>
        /// <exception cref="InvalidOperationException">No generation was authorized.</exception>
        internal Task WaitForGenerationAsync(int stepIndex = -1)
        {
            lock (_gate)
            {
                if (_generations.Count == 0)
                {
                    throw new InvalidOperationException("cannot use wait_for_generation: no active generation is running.");
                }

                return _generations[stepIndex < 0 ? _generations.Count + stepIndex : stepIndex].Task;
            }
        }

        /// <summary>Waits until the scheduler has queued this speech.</summary>
        internal Task WaitForScheduledAsync()
        {
            return _scheduled.Task;
        }

        /// <summary>Marks the latest generation done, which lets the scheduler move to the next speech.</summary>
        /// <exception cref="InvalidOperationException">No generation was authorized.</exception>
        internal void MarkGenerationDone()
        {
            lock (_gate)
            {
                if (_generations.Count == 0)
                {
                    throw new InvalidOperationException("cannot use mark_generation_done: no active generation is running.");
                }

                _ = _generations[^1].TrySetResult();
            }
        }

        /// <summary>Marks the speech done. The first call wins; later calls still close the generation and the backstop.</summary>
        /// <param name="error">Why the speech failed, kept for <see cref="Error"/> and never raised to an awaiter.</param>
        internal void MarkDone(Exception? error = null)
        {
            bool firstDone = false;
            lock (_gate)
            {
                if (!IsDone)
                {
                    _error = error ?? _error;
                    _ = _done.TrySetResult(this);
                    firstDone = true;
                }

                if (_generations.Count > 0)
                {
                    _ = _generations[^1].TrySetResult();
                }
            }

            _tasks.DisarmBackstop();
            if (firstDone)
            {
                _doneCallbacks.Fire();
            }
        }

        /// <summary>Releases <see cref="WaitForScheduledAsync"/> once the scheduler has queued this speech.</summary>
        internal void MarkScheduled()
        {
            _ = _scheduled.TrySetResult();
        }

        private static TaskCompletionSource NewSource()
        {
            return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private void CancelLocked()
        {
            if (IsDone || !_interrupted.TrySetResult())
            {
                return;
            }

            _tasks.ArmBackstop(_time, InterruptionTimeout, OnInterruptionTimeout);
        }

        private void OnInterruptionTimeout()
        {
            VoiceConversationLog.SpeechNotDoneAfterInterruption(_logger, Id, InterruptionTimeout.TotalSeconds);
            _tasks.Cancel();
            MarkDone();
        }
    }
}
