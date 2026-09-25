namespace AgentCore.Application.Configuration.Schema
{
    /// <summary>
    /// What one tool id's filler says while its step runs, and how often. Voice conversations only.
    /// </summary>
    public sealed record VoiceFillerConfiguration
    {
        /// <summary>Gets what the agent says while the tool call runs.</summary>
        public required string Say { get; init; }

        /// <summary>Gets how long the session must stay continuously idle before the first fire.</summary>
        public required double DelaySeconds { get; init; }

        /// <summary>Gets the cooldown after a fire before the dwell restarts, or null to fire at most once.</summary>
        public double? IntervalSeconds { get; init; }

        /// <summary>Gets the most fires across the tool call's lifetime, or null for no limit.</summary>
        public int? MaxSteps { get; init; }
    }
}
