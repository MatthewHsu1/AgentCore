using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tests.Runtime
{
    /// <summary>A model that calls the one tool it is given, every round, and never stops on its own.</summary>
    internal sealed class CallForever : IChatClient
    {
        private int _round;

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            int round = Interlocked.Increment(ref _round);
            string id = Guid.NewGuid().ToString("N");
            if (options?.Tools?.OfType<AIFunction>().FirstOrDefault() is not { } tool)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, "no tool") { ResponseId = id, MessageId = id };
                yield break;
            }

            yield return new ChatResponseUpdate(
                ChatRole.Assistant,
                [new TextContent($"Checking {round}. "), new FunctionCallContent($"call_{round}", tool.Name, new Dictionary<string, object?>())])
            {
                ResponseId = id,
                MessageId = id,
            };
        }

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            List<ChatResponseUpdate> updates = [];
            await foreach (ChatResponseUpdate update in GetStreamingResponseAsync(messages, options, cancellationToken))
            {
                updates.Add(update);
            }

            return updates.ToChatResponse();
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
