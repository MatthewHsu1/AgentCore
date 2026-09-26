using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace AgentCore.AspNetCore.Tests.Fakes
{
    /// <summary>
    /// A model that answers "reply to" the caller's last words. A request whose last words start with
    /// <see cref="RacePrefix"/> answers only once <paramref name="parties"/> such requests are waiting, so every racing
    /// request has read the conversation before any of them commits.
    /// </summary>
    /// <param name="parties">How many racing requests answer together.</param>
    internal sealed class RendezvousChatClient(int parties) : IChatClient
    {
        /// <summary>The words that make a request wait for the others.</summary>
        public const string RacePrefix = "race";

        private readonly TaskCompletionSource _allArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _arrived;

        /// <summary>Gets every request the model was sent, each as the texts of its messages, oldest first.</summary>
        public ConcurrentQueue<IReadOnlyList<string>> Requests { get; } = new();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            List<ChatMessage> sent = [.. messages];
            Requests.Enqueue([.. sent.Select(message => message.Text)]);
            string last = sent.LastOrDefault(message => message.Role == ChatRole.User)?.Text ?? string.Empty;

            if (last.StartsWith(RacePrefix, StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref _arrived) >= parties)
                {
                    _ = _allArrived.TrySetResult();
                }

                await _allArrived.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            string id = Guid.NewGuid().ToString("N");
            yield return new ChatResponseUpdate(ChatRole.Assistant, "reply to " + last) { ResponseId = id, MessageId = id };
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
            return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
        }

        public void Dispose()
        {
        }
    }
}
