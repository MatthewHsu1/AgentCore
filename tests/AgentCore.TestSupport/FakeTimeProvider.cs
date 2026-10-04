namespace AgentCore.TestSupport
{
    /// <summary>
    /// A clock a test owns, including every timer a production seam schedules against it.
    /// </summary>
    public sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private readonly Lock _gate = new();
        private readonly List<FakeTimer> _timers = [];
        private readonly List<(DateTimeOffset? DueAt, int Count, TaskCompletionSource Reached)> _waiters = [];
        private DateTimeOffset _now = start;

        /// <summary>Gets the zone this clock calls local; the machine's own unless a test names one.</summary>
        public TimeZoneInfo Zone { get; init; } = TimeZoneInfo.Local;

        public override TimeZoneInfo LocalTimeZone => Zone;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
            {
                return _now;
            }
        }

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp()
        {
            lock (_gate)
            {
                return _now.UtcTicks;
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ArgumentNullException.ThrowIfNull(callback);

            FakeTimer timer = new(this, callback, state);
            Schedule(timer, dueTime, period);
            return timer;
        }

        /// <summary>Waits until exactly <paramref name="count"/> live timers are due at <paramref name="dueAt"/>.</summary>
        /// <param name="dueAt">The instant the timers are due.</param>
        /// <param name="count">How many live timers to wait for.</param>
        /// <returns>A task that completes once the count matches.</returns>
        public Task WaitForTimersAsync(DateTimeOffset dueAt, int count)
        {
            return WaitForCountAsync(dueAt, count);
        }

        /// <summary>Waits until exactly <paramref name="count"/> live timers exist, whenever they are due.</summary>
        /// <param name="count">How many live timers to wait for.</param>
        /// <returns>A task that completes once the count matches.</returns>
        public Task WaitForTimersAsync(int count)
        {
            return WaitForCountAsync(null, count);
        }

        private Task WaitForCountAsync(DateTimeOffset? dueAt, int count)
        {
            lock (_gate)
            {
                if (CountLocked(dueAt) == count)
                {
                    return Task.CompletedTask;
                }

                TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Add((dueAt, count, reached));
                return reached.Task;
            }
        }

        /// <summary>Moves the clock forward, and fires every timer whose due time this reaches or passes.</summary>
        /// <param name="delta">How far to move the clock.</param>
        public void Advance(TimeSpan delta)
        {
            List<FakeTimer> due;
            lock (_gate)
            {
                _now += delta;
                due = [.. _timers.Where(timer => !timer.Disposed && timer.DueAt <= _now)];
            }

            foreach (FakeTimer timer in due)
            {
                timer.Fire();
            }
        }

        private void Schedule(FakeTimer timer, TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate)
            {
                timer.Period = period;
                if (dueTime == Timeout.InfiniteTimeSpan)
                {
                    _ = _timers.Remove(timer);
                }
                else
                {
                    timer.DueAt = _now + dueTime;
                    if (!_timers.Contains(timer))
                    {
                        _timers.Add(timer);
                    }
                }

                ReleaseWaitersLocked();
            }
        }

        /// <summary>
        /// Claims one firing of a timer that is still scheduled and due. A timer changed or disposed after
        /// <see cref="Advance"/> picked it does not fire.
        /// </summary>
        private bool TryTakeDue(FakeTimer timer)
        {
            lock (_gate)
            {
                if (timer.Disposed || timer.DueAt > _now || !_timers.Contains(timer))
                {
                    return false;
                }

                if (timer.Period == Timeout.InfiniteTimeSpan || timer.Period == TimeSpan.Zero)
                {
                    _ = _timers.Remove(timer);
                }
                else
                {
                    timer.DueAt = _now + timer.Period;
                }

                ReleaseWaitersLocked();
                return true;
            }
        }

        private void Remove(FakeTimer timer)
        {
            lock (_gate)
            {
                _ = _timers.Remove(timer);
                ReleaseWaitersLocked();
            }
        }

        private int CountLocked(DateTimeOffset? dueAt)
        {
            return _timers.Count(timer => !timer.Disposed && (dueAt is null || timer.DueAt == dueAt));
        }

        private void ReleaseWaitersLocked()
        {
            _ = _waiters.RemoveAll(waiter => CountLocked(waiter.DueAt) == waiter.Count && waiter.Reached.TrySetResult());
        }

        private sealed class FakeTimer(FakeTimeProvider owner, TimerCallback callback, object? state) : ITimer
        {
            public DateTimeOffset DueAt { get; set; }

            public TimeSpan Period { get; set; }

            public bool Disposed { get; private set; }

            /// <summary>Runs the callback, and reschedules only when this timer asked for a period.</summary>
            public void Fire()
            {
                if (owner.TryTakeDue(this))
                {
                    callback(state);
                }
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (Disposed)
                {
                    return false;
                }

                owner.Schedule(this, dueTime, period);
                return true;
            }

            public void Dispose()
            {
                Disposed = true;
                owner.Remove(this);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
