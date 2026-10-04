namespace AgentCore.Application.Hooks.Notices
{
    /// <summary>The audit token of each <see cref="TurnRefusal"/>, under <c>refusedReason</c>.</summary>
    public static class TurnRefusalTokens
    {
        /// <summary>Names one refusal reason.</summary>
        /// <param name="refusal">The reason.</param>
        /// <returns>The token the audit row and the log line carry.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="refusal"/> is outside the closed set.</exception>
        public static string ToToken(TurnRefusal refusal)
        {
            return refusal switch
            {
                TurnRefusal.Busy => "busy",
                TurnRefusal.Conflict => "conflict",
                TurnRefusal.Gone => "gone",
                TurnRefusal.Dropped => "dropped",
                TurnRefusal.Terminal => "terminal",
                TurnRefusal.Disposed => "disposed",
                TurnRefusal.InUse => "in_use",
                _ => throw new ArgumentOutOfRangeException(nameof(refusal), refusal, "The refusal is outside the closed set."),
            };
        }
    }
}
