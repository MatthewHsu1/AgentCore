using System.Runtime.CompilerServices;
using AgentCore.Application.Runtime.Turn;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Runtime.Compaction
{
    /// <summary>
    /// Wraps the summariser for one invocation and posts the start notice at the one point that
    /// actually means "the summariser is being called": the first request this client forwards.
    /// </summary>
    internal sealed class NoticingChatClient : DelegatingChatClient
    {
        private readonly TurnNotices? _notices;

        public NoticingChatClient(IChatClient inner, TurnNotices? notices)
            : base(inner)
        {
            _notices = notices;
        }

        /// <summary>Gets whether this client has forwarded a request yet.</summary>
        public bool Called { get; private set; }

        public override Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            NoteCall();
            return base.GetResponseAsync(messages, options, cancellationToken);
        }

        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            NoteCall();

            await foreach (ChatResponseUpdate update in base.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
            {
                yield return update;
            }
        }

        private void NoteCall()
        {
            if (Called)
            {
                return;
            }

            Called = true;
            _notices?.Post(new CompactionContent(CompactionContent.StartPhase));
        }
    }
}
