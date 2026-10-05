using Microsoft.Extensions.AI;

namespace AgentCore.AspNetCore.Tests.Fakes
{
    /// <summary>
    /// Calls, in one round and in the order offered, every tool whose result the request does not hold yet, then answers
    /// in words. With <see cref="OnePerRound"/>, each round calls only the first of them.
    /// </summary>
    internal sealed class MissingToolsChatClient : IChatClient
    {
        private int _requests;

        private int _callIds;

        public bool OnePerRound { get; init; }

        /// <summary>Gets a task that completes once a second request reached the model.</summary>
        public TaskCompletionSource SecondRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _requests) == 2)
            {
                _ = SecondRequest.TrySetResult();
            }

            await Task.Yield();

            List<AIContent> contents = [.. messages.SelectMany(message => message.Contents)];
            HashSet<string> answered = [.. contents.OfType<FunctionResultContent>().Select(result => result.CallId)];
            HashSet<string> ran = [.. contents.OfType<FunctionCallContent>().Where(call => answered.Contains(call.CallId)).Select(call => call.Name)];
            List<AIContent> calls =
            [
                .. (options?.Tools ?? []).OfType<AIFunction>()
                    .Where(tool => !ran.Contains(tool.Name))
                    .Take(OnePerRound ? 1 : int.MaxValue)
                    .Select(tool => new FunctionCallContent("call_" + Interlocked.Increment(ref _callIds), tool.Name, new Dictionary<string, object?>(StringComparer.Ordinal))),
            ];

            yield return calls.Count > 0
                ? new ChatResponseUpdate(ChatRole.Assistant, calls)
                : new ChatResponseUpdate(ChatRole.Assistant, "done.");
        }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
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
            return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
        }

        public void Dispose()
        {
        }
    }
}
