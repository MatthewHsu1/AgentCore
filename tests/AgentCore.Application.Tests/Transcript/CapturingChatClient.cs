using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tests.Transcript
{
    /// <summary>Answers each request in turn, keeps what it was asked, and runs a hook before answering.</summary>
    internal sealed class CapturingChatClient(Func<int, Task> beforeReply, params string[] replies) : IChatClient
    {
        private readonly Func<int, Task> _beforeReply = beforeReply;

        private readonly string[] _replies = replies;

        private int _calls;

        /// <summary>Gets every request this client answered, one role-prefixed line per message: its text, or its tool results.</summary>
        public List<List<string>> Requests { get; } = [];

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(messages);
            int callIndex = _calls++;
            Requests.Add([.. messages.Select(message => $"{message.Role}:{Words(message)}")]);

            await _beforeReply(callIndex).ConfigureAwait(false);

            return new ChatResponse(new ChatMessage(ChatRole.Assistant, _replies[callIndex]));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ChatResponse response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            foreach (ChatResponseUpdate update in response.ToChatResponseUpdates())
            {
                yield return update;
            }
        }

        private static string Words(ChatMessage message)
        {
            return message.Text.Length > 0
                        ? message.Text
                        : string.Join("|", message.Contents.OfType<FunctionResultContent>().Select(result => result.Result?.ToString()));
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return null;
        }

        public void Dispose()
        {
        }
    }
}
