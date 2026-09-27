namespace AgentCore.AspNetCore.Tests.Fakes
{
    /// <summary>Waits for a state no event announces. The test's own timeout bounds it.</summary>
    internal static class Poll
    {
        /// <summary>Waits until <paramref name="condition"/> holds.</summary>
        public static async Task UntilAsync(Func<bool> condition)
        {
            while (!condition())
            {
                await Task.Delay(1).ConfigureAwait(false);
            }
        }
    }
}
