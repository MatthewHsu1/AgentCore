using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tests.Runtime
{
    /// <summary>
    /// Reports that the extractor call is in flight, then holds it until the test lets it answer.
    /// </summary>
    internal sealed class GatedExtractorChatClient : IChatClient
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Gets a task that completes once the extractor's own model call is in flight.</summary>
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Lets the extractor answer.</summary>
        public void OpenGate()
        {
            _ = _gate.TrySetResult();
        }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(messages);
            _ = Started.TrySetResult();
            await _gate.Task.ConfigureAwait(false);

            // An empty document fills no slot, which is all this fake owes the writer order.
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "{}"));
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
