using AgentCore.Application.Transcript;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Runtime;

/// <summary>Takes what only the caller can use out of the history before the model sees it.</summary>
internal sealed class ModelFacingChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        => base.GetResponseAsync(Strip(messages), options, ct);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
        => base.GetStreamingResponseAsync(Strip(messages), options, ct);

    private static IEnumerable<ChatMessage> Strip(IEnumerable<ChatMessage> messages)
        => messages.Select(static message => message.WithoutHostContent());
}
