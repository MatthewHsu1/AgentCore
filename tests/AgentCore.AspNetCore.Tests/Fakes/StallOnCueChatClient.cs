using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace AgentCore.AspNetCore.Tests.Fakes
{
    /// <summary>
    /// A model that answers "reply to" the caller's last words, except words that start with <see cref="Cue"/>: those
    /// get one piece, then a stall that only the run's cancellation (a client abort) ends.
    /// </summary>
    internal sealed class StallOnCueChatClient : IChatClient
    {
        /// <summary>The words that make the reply stall.</summary>
        public const string Cue = "stall";

        /// <summary>The piece a stalled reply sends before it stalls.</summary>
        public const string Piece = "partial";

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            string last = messages.LastOrDefault(message => message.Role == ChatRole.User)?.Text ?? string.Empty;
            if (!last.StartsWith(Cue, StringComparison.Ordinal))
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, "reply to " + last);
                yield break;
            }

            yield return new ChatResponseUpdate(ChatRole.Assistant, Piece);
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
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
