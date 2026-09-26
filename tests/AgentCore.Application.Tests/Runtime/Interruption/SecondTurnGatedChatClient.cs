using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tests.Runtime
{
    /// <summary>
    /// Streams its first reply to the end, then blocks the second one before it says a word.
    /// </summary>
    internal sealed class SecondTurnGatedChatClient(string first, string second) : IChatClient
    {
        private readonly string _first = first;
        private readonly string _second = second;
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        /// <summary>Signals once the second turn's own model call is in flight and blocked.</summary>
        public TaskCompletionSource SecondTurnStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Lets the second reply flow.</summary>
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

            int index = Interlocked.Increment(ref _calls);
            string responseId = Guid.NewGuid().ToString("N");

            if (index > 1)
            {
                _ = SecondTurnStarted.TrySetResult();

                // Not WaitAsync(cancellationToken): a defect that cancelled this turn would then
                // look like the pass this test is trying to disprove.
                await _gate.Task.ConfigureAwait(false);
            }

            foreach (string? word in (index == 1 ? _first : _second).Split(' ')
                .Select((word, position) => position == 0 ? word : " " + word))
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, word)
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
