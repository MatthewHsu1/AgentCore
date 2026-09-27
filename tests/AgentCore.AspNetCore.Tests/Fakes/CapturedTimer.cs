namespace AgentCore.AspNetCore.Tests.Fakes
{
    /// <summary>One timer a <see cref="CapturingTimeProvider"/> created.</summary>
    internal sealed class CapturedTimer(TimerCallback callback, object? state) : ITimer
    {
        public bool Disposed { get; private set; }

        /// <summary>Runs the callback now, disposed or not.</summary>
        public void Fire()
        {
            callback(state);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            return !Disposed;
        }

        public void Dispose()
        {
            Disposed = true;
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
