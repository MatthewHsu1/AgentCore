using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tests.Runtime
{
    /// <summary>A model endpoint that answers every request with a transport fault, before any word or tool call.</summary>
    internal sealed class DownModelChatClient : IChatClient
    {
        /// <summary>The message every fault carries.</summary>
        public const string Message = "503 from the model endpoint";

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            throw new HttpRequestException(Message);
#pragma warning disable CS0162 // The iterator needs a yield to be one; the throw above always ends it first.
            yield break;
#pragma warning restore CS0162
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            throw new HttpRequestException(Message);
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return null;
        }

        public void Dispose()
        {
            // Nothing to release.
        }
    }
}
