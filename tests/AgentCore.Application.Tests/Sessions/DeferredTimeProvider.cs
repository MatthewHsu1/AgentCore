using AgentCore.TestSupport;

namespace AgentCore.Application.Tests.Sessions
{
    /// <summary>
    /// A fake clock whose timers come due on <see cref="FakeTimeProvider.Advance"/> but whose callbacks wait
    /// until the test runs them.
    /// </summary>
    internal sealed class DeferredTimeProvider(FakeTimeProvider clock) : TimeProvider
    {
        private readonly Queue<Action> _due = new();

        public override long TimestampFrequency => clock.TimestampFrequency;

        public override DateTimeOffset GetUtcNow()
        {
            return clock.GetUtcNow();
        }

        public override long GetTimestamp()
        {
            return clock.GetTimestamp();
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            return clock.CreateTimer(_ => _due.Enqueue(() => callback(state)), null, dueTime, period);
        }

        /// <summary>Runs every callback that came due, in the order they came due.</summary>
        /// <returns>How many callbacks ran.</returns>
        public int RunDue()
        {
            int ran = 0;
            while (_due.TryDequeue(out Action? callback))
            {
                callback();
                ran++;
            }

            return ran;
        }
    }
}
