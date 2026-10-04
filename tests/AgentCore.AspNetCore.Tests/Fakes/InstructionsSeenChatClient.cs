using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace AgentCore.AspNetCore.Tests.Fakes
{
    /// <summary>Records the instructions of every streamed request, in call order.</summary>
    internal sealed class InstructionsSeenChatClient(IChatClient inner) : DelegatingChatClient(inner)
    {
        private readonly List<string> _instructions = [];

        public IReadOnlyList<string> Instructions
        {
            get
            {
                lock (_instructions)
                {
                    return [.. _instructions];
                }
            }
        }

        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            lock (_instructions)
            {
                _instructions.Add(options?.Instructions ?? string.Empty);
            }

            await foreach (ChatResponseUpdate update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
            {
                yield return update;
            }
        }
    }
}
