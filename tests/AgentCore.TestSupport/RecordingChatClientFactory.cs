using System.Runtime.CompilerServices;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using Microsoft.Extensions.AI;

namespace AgentCore.TestSupport;

/// <summary>
/// Records the <see cref="ModelReference"/> it was asked for, and answers every request with a
/// fixed reply.
/// </summary>
public sealed class RecordingChatClientFactory : IChatClientFactory
{
    private readonly IChatClient _client;

    /// <summary>Creates the factory.</summary>
    /// <param name="client">The client every call answers with, or <see langword="null"/> for a
    /// stub that always replies <c>"ok"</c> and calls no tool.</param>
    public RecordingChatClientFactory(IChatClient? client = null) => _client = client ?? new StubChatClient();

    /// <summary>Gets the reference the last call passed, or <see langword="null"/> when no call has
    /// happened yet or the last call asked for the host default.</summary>
    public ModelReference? Asked { get; private set; }

    public IChatClient GetChatClient(ModelReference? model)
    {
        Asked = model;
        return _client;
    }

    private sealed class StubChatClient : IChatClient
    {
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(messages);
            await Task.Yield();

            var responseId = Guid.NewGuid().ToString("N");
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok")
            {
                ResponseId = responseId,
                MessageId = responseId,
            };
        }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            List<ChatResponseUpdate> updates = [];
            await foreach (var update in GetStreamingResponseAsync(messages, options, cancellationToken)
                .ConfigureAwait(false))
            {
                updates.Add(update);
            }

            return updates.ToChatResponse();
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            ArgumentNullException.ThrowIfNull(serviceType);
            return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
        }

        public void Dispose()
        {
        }
    }
}
