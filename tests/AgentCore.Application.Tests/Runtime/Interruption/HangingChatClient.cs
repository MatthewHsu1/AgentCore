using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tests.Runtime
{
    /// <summary>Answers nothing, ever. It stands in for the extractor model hanging past its deadline.</summary>
    internal sealed class HangingChatClient : IChatClient
    {
        private readonly TaskCompletionSource _neverSet = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(messages);
            await _neverSet.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

            // Unreachable: nothing ever completes _neverSet, so the wait above only ever cancels.
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Empty));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            // StateExtractor.ExtractAsync calls GetResponseAsync and never streams.
            throw new NotSupportedException("The extractor calls GetResponseAsync, and never streams.");
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            ArgumentNullException.ThrowIfNull(serviceType);
            return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
        }

        public void Dispose()
        {
            // Nothing to release.
        }
    }
}
