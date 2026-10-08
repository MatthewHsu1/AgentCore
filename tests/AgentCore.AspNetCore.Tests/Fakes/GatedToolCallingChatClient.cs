using Microsoft.Extensions.AI;

namespace AgentCore.AspNetCore.Tests.Fakes
{
    /// <summary>Calls the first tool it is offered, once, with <paramref name="arguments"/>, then answers in words.</summary>
    internal sealed class GatedToolCallingChatClient(IReadOnlyDictionary<string, object?>? arguments = null) : IChatClient
    {
        private static readonly Dictionary<string, object?> DefaultArguments = new(StringComparer.Ordinal) { ["to"] = "a@b.com" };

        private int _requests;

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

            bool alreadyCalled = messages.Any(message => message.Contents.OfType<FunctionResultContent>().Any());

            if (!alreadyCalled && options?.Tools?.OfType<AIFunction>().FirstOrDefault() is { } tool)
            {
                yield return new ChatResponseUpdate(
                    ChatRole.Assistant,
                    [new FunctionCallContent(
                          "conversation_1",
                          tool.Name,
                          new Dictionary<string, object?>(arguments ?? DefaultArguments, StringComparer.Ordinal))]);
                yield break;
            }

            yield return new ChatResponseUpdate(ChatRole.Assistant, "done.");
        }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            List<ChatResponseUpdate> updates = [];
            await foreach (ChatResponseUpdate? update in GetStreamingResponseAsync(messages, options, cancellationToken)
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
