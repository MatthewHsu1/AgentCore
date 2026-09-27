using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tests.Runtime
{
    /// <summary>
    /// Answers with one call, then, once that call's result is in context, answers with that same
    /// call again alongside a brand new one, both on a single message.
    /// </summary>
    internal sealed class ParallelToolCallChatClient : IChatClient
    {
        /// <summary>The id of the conversation whose result arrives before the barge-in.</summary>
        public const string FirstCallId = "conversation_a";

        /// <summary>The id of the conversation still in flight when the barge-in arrives.</summary>
        public const string SecondCallId = "conversation_b";

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(messages);
            await Task.Yield();

            HashSet<string> answered = messages
                .SelectMany(message => message.Contents.OfType<FunctionResultContent>())
                .Select(result => result.CallId)
                .ToHashSet(StringComparer.Ordinal);

            AIFunction tool = options?.Tools?.OfType<AIFunction>().FirstOrDefault()
                ?? throw new InvalidOperationException("The turn offers no tool to call.");
            string responseId = Guid.NewGuid().ToString("N");

            if (!answered.Contains(FirstCallId))
            {
                // Round one. One call, so the runtime's batch commits alone and finishes at once.
                yield return new ChatResponseUpdate(
                    ChatRole.Assistant,
                    [
                        new FunctionCallContent(
                            FirstCallId, tool.Name, new Dictionary<string, object?>(StringComparer.Ordinal) { ["index"] = 1 }),
                    ])
                {
                    ResponseId = responseId,
                    MessageId = responseId,
                };

                yield break;
            }

            // Round two. The already-answered conversation rides again beside the new one, on one message.
            yield return new ChatResponseUpdate(
                ChatRole.Assistant,
                [
                    new FunctionCallContent(
                        FirstCallId, tool.Name, new Dictionary<string, object?>(StringComparer.Ordinal) { ["index"] = 1 }),
                    new FunctionCallContent(
                        SecondCallId, tool.Name, new Dictionary<string, object?>(StringComparer.Ordinal) { ["index"] = 2 }),
                ])
            {
                ResponseId = responseId,
                MessageId = responseId,
            };
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
            ArgumentNullException.ThrowIfNull(serviceType);
            return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
        }

        public void Dispose()
        {
            // Nothing to release.
        }
    }
}
