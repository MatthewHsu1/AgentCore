using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tests.Fakes
{
    /// <summary>Answers each request in turn, and keeps what it was asked.</summary>
    internal sealed class RequestRecordingChatClient(params string[] replies) : IChatClient
    {
        private readonly string[] _replies = replies;

        /// <summary>Gets every request this client answered, one role-prefixed line per message.</summary>
        public List<List<string>> Requests { get; } = [];

        /// <summary>Gets the instructions of each request, in call order. A per-invocation context lands here.</summary>
        public List<string?> Instructions { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(messages);
            Requests.Add([.. messages.Select(message => $"{message.Role}:{message.Text}")]);
            Instructions.Add(options?.Instructions);
            return Task.FromResult(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, _replies[Requests.Count - 1]) { MessageId = $"m{Requests.Count}" }));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ChatResponse response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            foreach (ChatResponseUpdate update in response.ToChatResponseUpdates())
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return null;
        }

        public void Dispose()
        {
        }
    }
}
