// Portions derived from LiveKit Agents, livekit-agents/livekit/agents/voice/agent_activity.py,
// commit d8405f132e1bd960f298190c18daf81ffc1faf45. Copyright 2023 LiveKit, Inc.
// Licensed under the Apache License, Version 2.0. Modified: translated to C#; audio paths removed.

using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace AgentCore.AspNetCore.Voice
{
    /// <summary>
    /// Plays one speech at a time from the speech queue, and interrupts the current, queued and background
    /// speeches on request.
    /// </summary>
    /// <param name="sessionLock">The one lock of the voice session. Never held across an await.</param>
    /// <param name="logger">Where scheduling warnings and speech task faults are reported.</param>
    internal sealed class SpeechScheduler(Lock sessionLock, ILogger logger)
    {
        private readonly Lock _lock = sessionLock;

        private readonly SpeechQueue _speechQueue = new();

        private readonly BackgroundSpeeches _backgroundSpeeches = new();

        private readonly List<Task> _speechTasks = [];

        private readonly AsyncEvent _queueUpdated = new();

        private SpeechHandle? _currentSpeech;

        private bool _schedulingPaused = true;

        private Task? _schedulingTask;

        private Task? _userTurnTask;

        /// <summary>Gets the speech playing now, or <see langword="null"/>.</summary>
        public SpeechHandle? CurrentSpeech
        {
            get
            {
                lock (_lock)
                {
                    return _currentSpeech;
                }
            }
        }

        /// <summary>Gets whether no speech is queued and the current one, if any, is done.</summary>
        public bool NoPendingSpeech
        {
            get
            {
                lock (_lock)
                {
                    return _speechQueue.Count == 0 && (_currentSpeech is null || _currentSpeech.IsDone);
                }
            }
        }

        /// <summary>Gets whether a speech finished speaking but still waits for its tools.</summary>
        public bool HasBackgroundSpeeches
        {
            get
            {
                lock (_lock)
                {
                    return _backgroundSpeeches.Any;
                }
            }
        }

        /// <summary>Gets whether the scheduler is refusing new speech (a forced continuation still gets through).</summary>
        public bool SchedulingPaused
        {
            get
            {
                lock (_lock)
                {
                    return _schedulingPaused;
                }
            }
        }

        /// <summary>Gets the latest user-turn task <see cref="SetUserTurnTask"/> recorded, or <see langword="null"/>.</summary>
        public Task? UserTurnTask
        {
            get
            {
                lock (_lock)
                {
                    return _userTurnTask;
                }
            }
        }

        /// <summary>Records the latest user-turn task, so <see cref="WaitForIdleAsync"/> can wait for it (LiveKit
        /// <c>agent_activity.py:2092-2094</c>) without ever cancelling it.</summary>
        /// <param name="task">The task, or <see langword="null"/> to stop tracking one.</param>
        public void SetUserTurnTask(Task? task)
        {
            lock (_lock)
            {
                _userTurnTask = task;
            }
        }

        /// <summary>Starts a task that may create or play a speech, and links it to <paramref name="speechHandle"/>.</summary>
        /// <param name="body">The task body. A fault is logged, then left on the returned task.</param>
        /// <param name="speechHandle">The speech the task works for. It is marked done once all its tasks are.</param>
        /// <returns>The running task.</returns>
        public Task CreateSpeechTask(Func<Task> body, SpeechHandle? speechHandle = null)
        {
            Task task = Task.Run(TaskTeardown.LogExceptions(body, ex => VoiceConversationLog.SpeechTaskFaulted(logger, ex)));
            lock (_lock)
            {
                _speechTasks.Add(task);
            }

            speechHandle?.AddTask(task);

            // Attached after both lists hold the task, so the removal can never run before the add.
            _ = task.ContinueWith(
                (finished, state) => OnSpeechTaskDone(finished, (SpeechHandle?)state),
                speechHandle,
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
            return task;
        }

        /// <summary>Queues a speech to play.</summary>
        /// <param name="speech">The speech.</param>
        /// <param name="priority">Where it goes in the queue.</param>
        /// <param name="force">Queue it even while scheduling is pausing, as the next step of a tool reply must.</param>
        /// <exception cref="InvalidOperationException">Scheduling is paused and <paramref name="force"/> is not set.</exception>
        public void ScheduleSpeech(SpeechHandle speech, SpeechPriority priority, bool force = false)
        {
            lock (_lock)
            {
                if (_schedulingPaused && !force)
                {
                    _ = speech.Interrupt(force: true);
                    throw new InvalidOperationException(
                        "cannot schedule new speech, the speech scheduling is draining/pausing, the speech will be cancelled");
                }

                if (_schedulingTask is { IsCompleted: true })
                {
                    VoiceConversationLog.SchedulingTaskNotRunning(logger, speech.Id);
                    _ = speech.Interrupt(force: true);
                    return;
                }

                _speechQueue.Enqueue(speech, priority);
                speech.MarkScheduled();
                _queueUpdated.Set();
            }
        }

        /// <summary>Interrupts the background speeches, the current speech, and the queue up to the first protected speech.</summary>
        /// <param name="force">Interrupt even speeches that disallow interruptions.</param>
        /// <param name="source">Why, recorded on each speech that had no interruption yet.</param>
        /// <returns>A task that completes when every speech this interrupted is done.</returns>
        /// <exception cref="InvalidOperationException">
        /// The current speech disallows interruptions and <paramref name="force"/> is not set. The background
        /// speeches are already interrupted by then; the queue is not.
        /// </exception>
        public Task Interrupt(bool force = false, InterruptionSource source = InterruptionSource.Programmatic)
        {
            List<SpeechHandle> interrupted;
            lock (_lock)
            {
                interrupted = _backgroundSpeeches.InterruptAll(force, source);

                if (_currentSpeech is not null)
                {
                    _ = _currentSpeech.Interrupt(force, source);
                    interrupted.Add(_currentSpeech);
                }

                foreach (SpeechHandle speech in _speechQueue.SortedSnapshot())
                {
                    try
                    {
                        _ = speech.Interrupt(force, source);
                    }
                    catch (InvalidOperationException)
                    {
                        // The speeches behind it will play, so stopping here keeps the conversation contiguous.
                        VoiceConversationLog.QueuedSpeechNotInterruptible(logger, speech.Id);
                        break;
                    }

                    interrupted.Add(speech);
                }
            }

            if (interrupted.Count == 0)
            {
                return Task.CompletedTask;
            }

            TaskCompletionSource playoutDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
            foreach (SpeechHandle speech in interrupted)
            {
                speech.AddDoneCallback(finished =>
                {
                    if (interrupted.TrueForAll(static handle => handle.IsDone))
                    {
                        _ = playoutDone.TrySetResult();
                    }
                });
            }

            return playoutDone.Task;
        }

        /// <summary>Interrupts every background speech that allows it, or all of them with <paramref name="force"/>.</summary>
        /// <param name="force">Interrupt even speeches that disallow interruptions.</param>
        /// <param name="source">Why.</param>
        /// <returns>The speeches interrupted.</returns>
        public IReadOnlyList<SpeechHandle> InterruptBackgroundSpeeches(bool force = false, InterruptionSource source = InterruptionSource.Programmatic)
        {
            lock (_lock)
            {
                return _backgroundSpeeches.InterruptAll(force, source);
            }
        }

        /// <summary>Marks a speech as waiting for its tools after its words were spoken.</summary>
        /// <param name="speech">The speech.</param>
        public void AddBackgroundSpeech(SpeechHandle speech)
        {
            lock (_lock)
            {
                _backgroundSpeeches.Add(speech);
            }
        }

        /// <summary>Removes a speech <see cref="AddBackgroundSpeech"/> added, once its tools finished.</summary>
        /// <param name="speech">The speech.</param>
        public void RemoveBackgroundSpeech(SpeechHandle speech)
        {
            lock (_lock)
            {
                _backgroundSpeeches.Remove(speech);
            }
        }

        /// <summary>Starts the scheduling loop, if scheduling is paused. A new scheduler starts paused.</summary>
        public void ResumeScheduling()
        {
            lock (_lock)
            {
                if (!_schedulingPaused)
                {
                    return;
                }

                _schedulingPaused = false;
                _schedulingTask = Task.Run(TaskTeardown.LogExceptions(
                    SchedulingLoopAsync, ex => VoiceConversationLog.SchedulingLoopFaulted(logger, ex)));
            }
        }

        /// <summary>Stops taking new speeches, then waits until the loop has played the queue and every speech task ended.</summary>
        /// <returns>A task that completes when the scheduling loop has exited.</returns>
        public async Task PauseSchedulingAsync()
        {
            Task? schedulingTask;
            lock (_lock)
            {
                if (_schedulingPaused)
                {
                    return;
                }

                _schedulingPaused = true;
                _queueUpdated.Set();
                schedulingTask = _schedulingTask;
            }

            if (schedulingTask is not null)
            {
                await schedulingTask.ConfigureAwait(false);
            }
        }

        /// <summary>Waits until no speech is current or queued, and no user turn is still being completed.</summary>
        /// <param name="cancellationToken">Cancels this wait only, never a speech or the user turn.</param>
        /// <returns>A task that completes once the agent and the user side are both idle.</returns>
        public async Task WaitForIdleAsync(CancellationToken cancellationToken = default)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                SpeechHandle? speech;
                Task? userTurnTask;
                lock (_lock)
                {
                    userTurnTask = _userTurnTask is { IsCompleted: false } pending ? pending : null;
                    if (_currentSpeech is null && _speechQueue.Count == 0 && userTurnTask is null)
                    {
                        return;
                    }

                    speech = _currentSpeech;
                }

                // Shielded, as LiveKit's wait_for_idle shields the user-turn task: this wait may be
                // cancelled without cancelling the turn itself.
                if (userTurnTask is not null)
                {
                    await userTurnTask.WaitAsync(cancellationToken).ConfigureAwait(false);
                }

                if (speech is { HasGenerations: true })
                {
                    await speech.WaitForGenerationAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
                }

                await Task.Yield();
            }
        }

        private void OnSpeechTaskDone(Task task, SpeechHandle? speechHandle)
        {
            // Observed here: LogExceptions already logged the fault, and a user-turn task has nobody else to await it.
            _ = task.Exception;
            lock (_lock)
            {
                _ = _speechTasks.Remove(task);
            }

            speechHandle?.MarkDoneIfTasksDone();
            _queueUpdated.Set();
        }

        private async Task SchedulingLoopAsync()
        {
            while (true)
            {
                await _queueUpdated.WaitAsync().ConfigureAwait(false);

                // Cleared before the queue is read: a speech queued after this point sets the event again.
                _queueUpdated.Clear();

                while (TryTakeNextSpeech(out SpeechHandle? speech))
                {
                    await speech.WaitForGenerationAsync().ConfigureAwait(false);
                    lock (_lock)
                    {
                        _currentSpeech = null;
                    }
                }

                lock (_lock)
                {
                    if (_schedulingPaused && _speechTasks.Count == 0)
                    {
                        return;
                    }
                }
            }
        }

        private bool TryTakeNextSpeech([NotNullWhen(true)] out SpeechHandle? speech)
        {
            lock (_lock)
            {
                while (_speechQueue.TryDequeue(out speech))
                {
                    _currentSpeech = speech;
                    if (speech.TryAuthorizeGeneration())
                    {
                        return true;
                    }

                    // Done while queued: interrupted there, or cut by its backstop.
                    _currentSpeech = null;
                }

                return false;
            }
        }
    }
}
