using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace AgentCore.AspNetCore.Tests.Fakes
{
    /// <summary>
    /// Step one says "Hello there, ", holds until <see cref="Resume"/> or until its request is cancelled, then
    /// says more and calls the tool. A request after a tool result, or for any later words, answers at once.
    /// </summary>
    internal sealed class GatedStepChatClient : IChatClient
    {
        private readonly TaskCompletionSource _resume = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConcurrentQueue<IReadOnlyList<ChatMessage>> Requests { get; } = new();

        public void Resume()
        {
            _ = _resume.TrySetResult();
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            List<ChatMessage> sent = [.. messages.Where(message => message.Role != ChatRole.System)];
            Requests.Enqueue(sent);
            string id = Guid.NewGuid().ToString("N");
            ChatMessage last = sent[^1];

            if (last.Role == ChatRole.Tool)
            {
                yield return Text("Done.", id);
                yield break;
            }

            if (last.Text != "hi")
            {
                yield return Text("Sure.", id);
                yield break;
            }

            yield return Text("Hello there, ", id);
            _ = Held.TrySetResult();
            try
            {
                await _resume.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _ = Stopped.TrySetResult();
                throw;
            }

            yield return Text("and more.", id);
            yield return new ChatResponseUpdate(
                ChatRole.Assistant,
                [new FunctionCallContent("call_1", options!.Tools!.OfType<AIFunction>().First().Name, new Dictionary<string, object?>())])
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
            return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
        }

        public void Dispose()
        {
        }

        private static ChatResponseUpdate Text(string text, string id)
        {
            return new ChatResponseUpdate(ChatRole.Assistant, text) { ResponseId = id, MessageId = id };
        }
    }
}
