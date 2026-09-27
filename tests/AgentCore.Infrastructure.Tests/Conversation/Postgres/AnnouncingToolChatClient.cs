using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace AgentCore.Infrastructure.Tests.Conversation.Postgres
{
    /// <summary>
    /// Says <c>"Checking n. "</c> and calls the first tool on the same message, round <c>n</c>. Once a tool answered,
    /// it speaks <paramref name="answer"/> word by word instead, unless <paramref name="answer"/> is
    /// <see langword="null"/>: then it calls the tool again every round.
    /// </summary>
    internal sealed class AnnouncingToolChatClient(string? answer) : IChatClient
    {
        private int _rounds;

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            List<ChatMessage> request = [.. messages];
            await Task.Yield();
            int round = Interlocked.Increment(ref _rounds);
            string id = Guid.NewGuid().ToString("N");

            AIFunction? tool = options?.Tools?.OfType<AIFunction>().FirstOrDefault();
            if (tool is null || (answer is not null && request.Count > 0 && request[^1].Role == ChatRole.Tool))
            {
                string[] words = (answer ?? string.Empty).Split(' ');
                for (int index = 0; index < words.Length; index++)
                {
                    yield return new ChatResponseUpdate(ChatRole.Assistant, index == 0 ? words[index] : " " + words[index])
                    {
                        ResponseId = id,
                        MessageId = id,
                    };
                }

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
