using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tests.Fakes
{
    /// <summary>
    /// A model that makes every given call at once, in one answer, while the transcript holds no tool result; every
    /// other request is answered with text.
    /// </summary>
    /// <param name="reply">The text answer.</param>
    /// <param name="calls">The tool name and call id of each call, in order.</param>
    internal sealed class ParallelToolChatClient(string reply, params (string Tool, string CallId)[] calls) : IChatClient
    {
        private int _calls;

        /// <summary>Gets how many requests reached the model.</summary>
        public int Calls => Volatile.Read(ref _calls);

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _ = Interlocked.Increment(ref _calls);
            bool answered = messages.Any(message => message.Contents.OfType<FunctionResultContent>().Any());
            await Task.Yield();

            string id = Guid.NewGuid().ToString("N");
            if (!answered)
            {
                yield return new ChatResponseUpdate(
                    ChatRole.Assistant,
                    [.. calls.Select(call => (AIContent)new FunctionCallContent(call.CallId, call.Tool, new Dictionary<string, object?>()))])
                {
                    ResponseId = id,
                    MessageId = id,
                };
                yield break;
            }

            yield return new ChatResponseUpdate(ChatRole.Assistant, reply) { ResponseId = id, MessageId = id };
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
