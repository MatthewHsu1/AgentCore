namespace AgentCore.AspNetCore.Voice
{
    /// <summary>
    /// What the agent says, for one conversation.
    /// </summary>
    public interface IConversationOutputPort : IAsyncDisposable
    {
        /// <summary>Starts one reply. Called before the reply's first <see cref="SpeakAsync"/>.</summary>
        void BeginReply();

        /// <summary>Speaks one fragment of the reply.</summary>
        /// <param name="fragment">The text to speak. Never empty.</param>
        /// <param name="cancellationToken">Stops waiting for room to send.</param>
        ValueTask SpeakAsync(string fragment, CancellationToken cancellationToken = default);

        /// <summary>Closes one reply, so the synthesizer knows no more is coming.</summary>
        /// <param name="cancellationToken">Stops waiting for room to send.</param>
        ValueTask CompleteAsync(CancellationToken cancellationToken = default);

        /// <summary>Starts stopping the reply, and drops whatever has not been spoken.</summary>
        /// <param name="cancellationToken">Stops waiting on the transport.</param>
        ValueTask StopAsync(CancellationToken cancellationToken = default);
    }
}
