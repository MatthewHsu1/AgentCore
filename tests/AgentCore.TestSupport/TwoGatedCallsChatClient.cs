using Microsoft.Extensions.AI;

namespace AgentCore.TestSupport
{
    /// <summary>
    /// Calls every tool it is offered in one round, then replies with each call's result as
    /// <c>call_N=result</c> lines, one per call in call-id order, so a test reads what the model was told about each call.
    /// </summary>
    public sealed class TwoGatedCallsChatClient : IChatClient
    {
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();

            List<FunctionResultContent> results = [.. messages.SelectMany(message => message.Contents.OfType<FunctionResultContent>())];
            if (results.Count == 0 && options?.Tools?.OfType<AIFunction>().ToList() is { Count: > 0 } tools)
            {
                yield return new ChatResponseUpdate(
                    ChatRole.Assistant,
                    [.. tools.Select((tool, index) => new FunctionCallContent(
                        $"call_{index + 1}",
                        tool.Name,
                        new Dictionary<string, object?>(StringComparer.Ordinal) { ["to"] = "a@b.com" }))]);
                yield break;
            }

            yield return new ChatResponseUpdate(
                ChatRole.Assistant,
                string.Join('\n', results.OrderBy(result => result.CallId, StringComparer.Ordinal).Select(result => $"{result.CallId}={result.Result}")));
        }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            List<ChatResponseUpdate> updates = [];
            await foreach (ChatResponseUpdate update in GetStreamingResponseAsync(messages, options, cancellationToken)
                .ConfigureAwait(false))
            {
                updates.Add(update);
            }

            return updates.ToChatResponse();
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
        }

        public void Dispose()
        {
        }
    }
}
