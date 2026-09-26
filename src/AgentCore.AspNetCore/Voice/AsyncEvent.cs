namespace AgentCore.AspNetCore.Voice
{
    /// <summary>
    /// An async gate: waiters block until <see cref="Set"/> opens it, and <see cref="Clear"/> closes it
    /// again for the next round. The .NET stand-in for Python's <c>asyncio.Event</c>, which LiveKit uses
    /// for its speech authorization gate and its scheduling and filler wake-ups.
    /// </summary>
    internal sealed class AsyncEvent
    {
        private readonly Lock _gate = new();

        private TaskCompletionSource _source = NewSource();

        /// <summary>Gets whether the event is set right now.</summary>
        public bool IsSet
        {
            get
            {
                lock (_gate)
                {
                    return _source.Task.IsCompleted;
                }
            }
        }

        /// <summary>Opens the gate. Every waiter, current or future until the next <see cref="Clear"/>, completes.</summary>
        public void Set()
        {
            lock (_gate)
            {
                _source.TrySetResult();
            }
        }

        /// <summary>Closes the gate for the next round. Waiters already released by a prior <see cref="Set"/> keep running.</summary>
        public void Clear()
        {
            lock (_gate)
            {
                if (_source.Task.IsCompleted)
                {
                    _source = NewSource();
                }
            }
        }

        /// <summary>Waits until the event is set.</summary>
        /// <param name="cancellationToken">Cancels the wait. The event's own state is untouched.</param>
        public Task WaitAsync(CancellationToken cancellationToken = default)
        {
            Task task;
            lock (_gate)
            {
                task = _source.Task;
            }

            return task.WaitAsync(cancellationToken);
        }

        private static TaskCompletionSource NewSource()
        {
            return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
