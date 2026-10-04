using System.Runtime.CompilerServices;
using AgentCore.Application.Tests.Runtime;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tests.Transcript
{
    /// <summary>
    /// Answers <c>noted</c> to every request, and holds the first request that carries <see cref="HeldWords"/> until
    /// the test releases it, so another session can save the same turn first. With <see cref="FaultsHeldRequest"/>, the
    /// held request ends in a transport fault instead of an answer.
    /// </summary>
    internal sealed class HeldFirstTurnChatClient(string heldWords) : IChatClient
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Gets the words whose first request is held.</summary>
        public string HeldWords { get; } = heldWords;

        /// <summary>Completes once the held request arrived.</summary>
        public TaskCompletionSource Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Gets a value indicating whether the held request, once released, throws instead of answering.</summary>
        public bool FaultsHeldRequest { get; init; }

        /// <summary>Gets a value indicating whether the held request, once released, answers with no text.</summary>
        public bool EmptiesHeldRequest { get; init; }

        /// <summary>Gets the last request the model was sent.</summary>
        public IReadOnlyList<ChatMessage> LastRequest { get; private set; } = [];

        /// <summary>Lets the held request answer.</summary>
        public void Release()
        {
            _ = _gate.TrySetResult();
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            List<ChatMessage> request = [.. messages];
            LastRequest = request;
            if (!_gate.Task.IsCompleted && request.Any(message => message.Text == HeldWords))
            {
                _ = Held.TrySetResult();
                await _gate.Task.WaitAsync(cancellationToken);
                if (FaultsHeldRequest)
                {
                    throw new HttpRequestException(DownModelChatClient.Message);
                }

                if (EmptiesHeldRequest)
                {
                    yield break;
                }
            }

            string id = Guid.NewGuid().ToString("N");
            yield return new ChatResponseUpdate(ChatRole.Assistant, "noted") { ResponseId = id, MessageId = id };
        }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            List<ChatResponseUpdate> updates = [];
            await foreach (ChatResponseUpdate update in GetStreamingResponseAsync(messages, options, cancellationToken))
            {
                updates.Add(update);
            }

            return updates.ToChatResponse();
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return null;
        }

        public void Dispose()
        {
            // Nothing to release.
        }
    }
}
