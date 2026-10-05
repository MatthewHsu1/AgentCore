using System.Runtime.CompilerServices;
using AgentCore.Application.Llm;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tests.Hooks
{
    /// <summary>Records what each streamed model request carried, in call order.</summary>
    internal sealed class RequestsSeen(IChatClient inner) : DelegatingChatClient(inner)
    {
        public List<Request> Requests { get; } = [];

        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            List<ChatMessage> sent = [.. messages];
            Record(sent, options);

            await foreach (ChatResponseUpdate update in base.GetStreamingResponseAsync(sent, options, cancellationToken))
            {
                yield return update;
            }
        }

        public override Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            List<ChatMessage> sent = [.. messages];
            Record(sent, options);
            return base.GetResponseAsync(sent, options, cancellationToken);
        }

        private void Record(List<ChatMessage> sent, ChatOptions? options)
        {
            lock (Requests)
            {
                Requests.Add(new Request(
                    options?.Instructions ?? string.Empty,
                    [.. sent.Select(static message => message.Text)],
                    [.. options?.Tools?.Select(static tool => tool.Name) ?? []],
                    options?.AdditionalProperties?.TryGetValue(ChatRequestProperties.ConversationId, out string? stamped) == true ? stamped : null));
            }
        }

        /// <summary>What one model request carried.</summary>
        /// <param name="Instructions">The instructions, or empty.</param>
        /// <param name="Messages">The text of each message.</param>
        /// <param name="Tools">The name of each tool offered.</param>
        /// <param name="Stamp">The conversation id stamped for the vendor, or <see langword="null"/>.</param>
        internal sealed record Request(string Instructions, IReadOnlyList<string> Messages, IReadOnlyList<string> Tools, string? Stamp);
    }
}
