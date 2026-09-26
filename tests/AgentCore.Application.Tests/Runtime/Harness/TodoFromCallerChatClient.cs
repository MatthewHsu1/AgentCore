using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tests.Runtime.Harness
{
    /// <summary>
    /// Adds a todo when the caller's latest words are <c>add &lt;title&gt;</c>, and answers <c>added</c> once the tool
    /// answered or when the caller asked anything else. It keys on the request alone, so any number of sessions can
    /// share one instance in any order.
    /// </summary>
    internal sealed class TodoFromCallerChatClient : IChatClient
    {
        private const string Add = "add ";

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            List<ChatMessage> request = [.. messages];
            await Task.Yield();
            string id = Guid.NewGuid().ToString("N");

            ChatMessage? said = request.LastOrDefault(message => message.Role == ChatRole.User && !message.Text.StartsWith('#'));
            bool answered = request.Count > 0 && request[^1].Role == ChatRole.Tool;
            AIFunction? tool = options?.Tools?.OfType<AIFunction>().FirstOrDefault(function => function.Name == "todos_add");

            if (!answered && tool is not null && said is not null && said.Text.StartsWith(Add, StringComparison.Ordinal))
            {
                yield return new ChatResponseUpdate(
                    ChatRole.Assistant,
                    [
                        new FunctionCallContent(
                            id,
                            tool.Name,
                            new Dictionary<string, object?>(StringComparer.Ordinal)
                            {
                                ["todos"] = new List<object>
                                {
                                    new Dictionary<string, object?>(StringComparer.Ordinal) { ["title"] = said.Text[Add.Length..] },
                                },
                            }),
                    ])
                {
                    ResponseId = id,
                    MessageId = id,
                };
                yield break;
            }

            yield return new ChatResponseUpdate(ChatRole.Assistant, "added") { ResponseId = id, MessageId = id };
        }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            List<ChatResponseUpdate> updates = [];
            await foreach (ChatResponseUpdate update in GetStreamingResponseAsync(messages, options, cancellationToken))
            {
                updates.Add(update);
            }

            return updates.ToChatResponse();
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
