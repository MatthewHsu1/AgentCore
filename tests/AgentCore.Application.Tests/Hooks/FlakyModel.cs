using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tests.Hooks
{
    /// <summary>Streams "final", " text". Each attempt may throw first, after the first update, or not at all.</summary>
    internal sealed class FlakyModel(Func<int, FlakyModel.Fault> script) : IChatClient
    {
        internal enum Fault
        {
            None,
            BeforeFirstUpdate,
            AfterFirstUpdate,
        }

        private int _attempts;

        public int Attempts => Volatile.Read(ref _attempts);

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            List<ChatResponseUpdate> updates = [];
            await foreach (ChatResponseUpdate update in GetStreamingResponseAsync(messages, options, cancellationToken))
            {
                updates.Add(update);
            }

            return updates.ToChatResponse();
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Fault fault = script(Interlocked.Increment(ref _attempts) - 1);
            await Task.Yield();
            if (fault == Fault.BeforeFirstUpdate)
            {
                throw new InvalidOperationException("model down");
            }

            yield return new ChatResponseUpdate(ChatRole.Assistant, "final") { MessageId = "m" };
            if (fault == Fault.AfterFirstUpdate)
            {
                throw new InvalidOperationException("model dropped");
            }

            yield return new ChatResponseUpdate(ChatRole.Assistant, " text") { MessageId = "m" };
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
