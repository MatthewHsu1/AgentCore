using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tests.Runtime
{
    /// <summary>
    /// Speaks one line and calls the tool on the same message, every round, so a tool that keeps failing spends
    /// its whole retry budget after the model already spoke. Round <c>n</c> says <c>"Checking n. "</c>.
    /// </summary>
    internal sealed class ProseThenFailingToolChatClient : IChatClient
    {
        private int _calls;

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            int round = Interlocked.Increment(ref _calls);
            string id = Guid.NewGuid().ToString("N");

            if (options?.Tools?.OfType<AIFunction>().FirstOrDefault() is not { } tool)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, "{}") { ResponseId = id, MessageId = id };
                yield break;
            }

            yield return new ChatResponseUpdate(
                ChatRole.Assistant,
                [
                    new TextContent($"Checking {round}. "),
                    new FunctionCallContent($"call_{round}", tool.Name, new Dictionary<string, object?>(StringComparer.Ordinal)),
                ])
            {
                ResponseId = id,
                MessageId = id,
            };
        }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            List<ChatResponseUpdate> updates = [];
            await foreach (ChatResponseUpdate update in GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
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
