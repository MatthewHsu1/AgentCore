using AgentCore.AspNetCore.Voice.Ports;

namespace AgentCore.AspNetCore.Voice.Routing
{
    /// <summary>
    /// The two halves of one conversation, opened together and disposed together.
    /// </summary>
    /// <param name="Input">What the caller does.</param>
    /// <param name="Output">What the agent says.</param>
    public sealed record ConversationChannel(IConversationInputPort Input, IConversationOutputPort Output) : IAsyncDisposable
    {
        private bool _disposed;

        /// <summary>Disposes both halves, and each underlying object exactly once.</summary>
        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            await Input.DisposeAsync().ConfigureAwait(false);

            if (!ReferenceEquals(Input, Output))
            {
                await Output.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
