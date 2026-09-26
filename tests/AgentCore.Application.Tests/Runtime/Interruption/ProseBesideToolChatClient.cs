using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tests.Runtime
{
    /// <summary>
    /// Announces the tool in prose and calls it, both on one assistant message, then streams its
    /// final reply and blocks after the first fragment until the test opens the gate.
    /// </summary>
    internal sealed class ProseBesideToolChatClient(string reply) : IChatClient
    {
        /// <summary>The line the model speaks before it calls the tool.</summary>
        public const string Prose = "Let me check that for you";

        private const string ToolCallId = "conversation_1";

        private readonly string[] _fragments = reply.Split(' ');
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Lets every fragment after the first flow.</summary>
        public void OpenGate()
        {
            _ = _gate.TrySetResult();
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(messages);
            await Task.Yield();

            bool answered = messages.Any(message => message.Contents.OfType<FunctionResultContent>().Any());
            string responseId = Guid.NewGuid().ToString("N");

            if (!answered && options?.Tools?.OfType<AIFunction>().FirstOrDefault() is { } tool)
            {
                // The prose and the conversation ride one message, which is what a real model produces.
                yield return new ChatResponseUpdate(
                    ChatRole.Assistant,
                    [
                        new TextContent(Prose),
                        new FunctionCallContent(ToolCallId, tool.Name, new Dictionary<string, object?>(StringComparer.Ordinal)),
                    ])
                {
                    ResponseId = responseId,
                    MessageId = responseId,
                };

                yield break;
            }

            for (int index = 0; index < _fragments.Length; index++)
            {
                if (index == 1)
                {
                    // The tool round already finished, so this is the round a barge-in interrupts.
                    await _gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }

                string text = index == 0 ? _fragments[index] : " " + _fragments[index];
                yield return new ChatResponseUpdate(ChatRole.Assistant, text)
                {
                    ResponseId = responseId,
                    MessageId = responseId,
                };
            }
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
