namespace AgentCore.AspNetCore.Conversation
{
    /// <summary>Settings for <see cref="ConversationSweeper"/>.</summary>
    public sealed class ConversationSweepOptions
    {
        /// <summary>How long a conversation is kept after its last activity, before the sweep erases it.</summary>
        public TimeSpan Retention { get; set; } = TimeSpan.FromDays(90);

        /// <summary>How often the sweep runs.</summary>
        public TimeSpan Interval { get; set; } = TimeSpan.FromHours(1);

        /// <summary>How many conversations one sweep pass erases at most.</summary>
        public int BatchSize { get; set; } = 500;
    }
}
