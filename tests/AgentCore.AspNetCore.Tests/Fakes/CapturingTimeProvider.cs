namespace AgentCore.AspNetCore.Tests.Fakes
{
    /// <summary>A clock that never fires on its own: a test calls a captured timer's callback itself, even
    /// after that timer was disposed, as .NET may run a callback it dispatched just before the dispose.</summary>
    internal sealed class CapturingTimeProvider : TimeProvider
    {
        private readonly Lock _gate = new();
        private readonly List<CapturedTimer> _timers = [];

        /// <summary>Gets every timer created so far, oldest first.</summary>
        public IReadOnlyList<CapturedTimer> Timers
        {
            get { lock (_gate) { return [.. _timers]; } }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            CapturedTimer timer = new(callback, state);
            lock (_gate)
            {
                _timers.Add(timer);
            }

            return timer;
        }
    }
}
