namespace AgentCore.AspNetCore.Voice
{
    /// <summary>What one voice conversation does on its own: the away prompt and the per-tool filler.</summary>
    /// <param name="UserAway">When the caller counts as away and what the agent says then, or <see langword="null"/> to disable it.</param>
    /// <param name="Fillers">The filler each tool id opens while its call runs. Empty means no filler.</param>
    internal sealed record VoiceOptions(UserAwayOptions? UserAway, IReadOnlyDictionary<string, FillerOptions> Fillers)
    {
        /// <summary>The budget <see cref="IConversationOutputPort.StopAsync"/> gives a transport to report what was heard: 2 s.</summary>
        public static readonly TimeSpan DefaultHeardTextWait = TimeSpan.FromSeconds(2);

        /// <summary>Gets the ruled defaults: away after 15 s with "Are you still there?", and no filler.</summary>
        public static VoiceOptions Default { get; } = new(
            new UserAwayOptions(TimeSpan.FromSeconds(15), "Are you still there?"),
            new Dictionary<string, FillerOptions>());

        /// <summary>
        /// Gets how long a reply a final prompt cut mid-step waits for the transport's report of what the caller
        /// heard before it cuts its turn to the text it forwarded. Not in the agent document.
        /// </summary>
        public TimeSpan HeardTextWait { get; init; } = DefaultHeardTextWait;
    }
}
