using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tests.Transcript
{
    /// <summary>
    /// The extractor's model: fills <c>callerName</c> from the last user line that says "I am ...", and leaves it
    /// unset otherwise.
    /// </summary>
    internal sealed class CallerNameFillChatClient : IChatClient
    {
        private const string Introduction = "I am ";

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            string? said = messages
                .SelectMany(message => message.Text.Split('\n'))
                .LastOrDefault(line => line.Contains(Introduction, StringComparison.Ordinal));
            string json = said is null
                ? """{ "callerName": null }"""
                : $$"""{ "callerName": "{{said[(said.IndexOf(Introduction, StringComparison.Ordinal) + Introduction.Length)..].Trim()}}" }""";
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, json)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ChatResponse response = await GetResponseAsync(messages, options, cancellationToken);
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
            // Nothing to release.
        }
    }
}
