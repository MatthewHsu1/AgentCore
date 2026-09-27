using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tests.Fakes
{
    /// <summary>A model that, once armed, holds its next call until the test opens the gate.</summary>
    internal sealed class GatedChatClient(IChatClient inner) : DelegatingChatClient(inner)
    {
        private volatile bool _armed;

        /// <summary>Completes when an armed call reaches the gate.</summary>
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Opens the gate.</summary>
        public TaskCompletionSource Open { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Makes every later call wait at the gate.</summary>
        public void Arm()
        {
            _armed = true;
        }

        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await WaitAtTheGateAsync(cancellationToken).ConfigureAwait(false);

            await foreach (ChatResponseUpdate update in base.GetStreamingResponseAsync(messages, options, cancellationToken)
                .ConfigureAwait(false))
            {
                yield return update;
            }
        }

        public override async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            await WaitAtTheGateAsync(cancellationToken).ConfigureAwait(false);

            return await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        }

        private async Task WaitAtTheGateAsync(CancellationToken cancellationToken)
        {
            if (_armed)
            {
                _ = Entered.TrySetResult();
                await Open.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
