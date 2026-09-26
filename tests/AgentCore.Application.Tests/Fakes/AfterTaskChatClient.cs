using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tests.Fakes
{
    /// <summary>
    /// Answers a request that carries a tool result only once <paramref name="first"/> completed, and fails it after
    /// 10 s. Every other request passes straight through.
    /// </summary>
    /// <param name="inner">The model that answers.</param>
    /// <param name="first">What must happen before the answer to a tool result.</param>
    internal sealed class AfterTaskChatClient(IChatClient inner, Task first) : DelegatingChatClient(inner)
    {
        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            List<ChatMessage> transcript = [.. messages];
            if (transcript.SelectMany(message => message.Contents).OfType<FunctionResultContent>().Any())
            {
                await first.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            }

            await foreach (ChatResponseUpdate update in base.GetStreamingResponseAsync(transcript, options, cancellationToken).ConfigureAwait(false))
            {
                yield return update;
            }
        }
    }
}
