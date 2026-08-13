namespace AgentCore.AspNetCore.Tests.Fakes;

/// <summary>
/// A clock a test owns, including every timer a production seam schedules against it.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AgentCore.AspNetCore.Vendors.TelnyxRelay.TelnyxRelayConnection"/> bounds its idle
/// deadline with <c>Task.Delay(delay, timeProvider, cancellationToken)</c>, and that overload arms
/// its completion through <see cref="TimeProvider.CreateTimer"/>, not through
/// <see cref="TimeProvider.GetUtcNow"/>. A fake that only overrode <c>GetUtcNow</c> would leave
/// that timer running on the real clock underneath it, so a test would still have to sleep for the
/// real delay. Overriding <see cref="CreateTimer"/> here is what lets <see cref="Advance"/> fire a
/// due timer synchronously, with no wall-clock wait at all.
/// </para>
/// <para>
/// Every timer this class hands out is one-shot in practice — the idle deadline is the only
/// production caller today, and it always asks for <see cref="Timeout.InfiniteTimeSpan"/> as its
/// period — but <see cref="Advance"/> still reschedules a periodic timer for its next due time,
/// so a future caller that does ask for a period is not silently starved.
/// </para>
/// </remarks>
internal sealed class FakeTimeProvider : TimeProvider
{
    private readonly Lock _gate = new();
    private readonly List<FakeTimer> _timers = [];
    private DateTimeOffset _now;

    public FakeTimeProvider(DateTimeOffset start) => _now = start;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var timer = new FakeTimer(this, callback, state);
        lock (_gate)
        {
            timer.DueAt = _now + dueTime;
            timer.Period = period;
            _timers.Add(timer);
        }

        return timer;
    }

    /// <summary>Moves the clock forward, and fires every timer whose due time this reaches or passes.</summary>
    /// <param name="delta">How far to move the clock.</param>
    /// <remarks>
    /// Firing happens outside the lock: a timer's own callback here is
    /// <see cref="CancellationTokenSource.Cancel()"/>, which can run a caller's registered
    /// callback synchronously, and nothing this class does may still hold <see cref="_gate"/>
    /// when a caller's own code starts running under it.
    /// </remarks>
    public void Advance(TimeSpan delta)
    {
        List<FakeTimer> due;
        lock (_gate)
        {
            _now += delta;
            due = [.. _timers.Where(timer => !timer.Disposed && timer.DueAt <= _now)];
        }

        foreach (var timer in due)
        {
            timer.Fire();
        }
    }

    private void Remove(FakeTimer timer)
    {
        lock (_gate)
        {
            _timers.Remove(timer);
        }
    }

    private sealed class FakeTimer(FakeTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset DueAt { get; set; }

        public TimeSpan Period { get; set; }

        public bool Disposed { get; private set; }

        /// <summary>Runs the callback, and reschedules only when this timer asked for a period.</summary>
        public void Fire()
        {
            if (Disposed)
            {
                return;
            }

            if (Period == Timeout.InfiniteTimeSpan || Period == TimeSpan.Zero)
            {
                owner.Remove(this);
                Disposed = true;
            }
            else
            {
                DueAt = owner.GetUtcNow() + Period;
            }

            callback(state);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (Disposed)
            {
                return false;
            }

            DueAt = owner.GetUtcNow() + dueTime;
            Period = period;
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
