namespace AgentCore.AspNetCore.Tests.Fakes
{
    /// <summary>The system clock, except that its first read runs an action first: a way into a window with no await in it.</summary>
    internal sealed class ActingTimeProvider(Action act) : TimeProvider
    {
        private int _acted;

        public override DateTimeOffset GetUtcNow()
        {
            if (Interlocked.Exchange(ref _acted, 1) == 0)
            {
                act();
            }

            return base.GetUtcNow();
        }
    }
}
