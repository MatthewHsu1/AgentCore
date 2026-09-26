namespace AgentCore.AspNetCore.Voice
{
    /// <summary>The longest span any <see cref="TimeProvider"/>-backed wait in this assembly accepts.</summary>
    internal static class BoundedTimerLimits
    {
        /// <summary>
        /// One millisecond short of <see cref="uint.MaxValue"/> — about 49.7 days — confirmed on net10 for
        /// <c>Task.Delay</c>, <c>CancelAfter</c>, and <c>Task.WaitAsync</c> alike. A document value above
        /// this is refused at load, where the error still carries a pointer into the document, rather than
        /// left to throw from inside a running conversation.
        /// </summary>
        public static readonly TimeSpan MaximumBoundedDelay = TimeSpan.FromMilliseconds(uint.MaxValue - 1);
    }
}
