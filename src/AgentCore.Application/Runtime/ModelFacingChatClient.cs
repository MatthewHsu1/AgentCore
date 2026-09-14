using System.Runtime.CompilerServices;
using AgentCore.Application.Transcript;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Runtime;

/// <summary>Takes what only the caller can use out of the history before the model sees it.</summary>
internal sealed class ModelFacingChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var _ = KeyHider.Hide(options);
        return await base.GetResponseAsync(Strip(messages), options, cancellationToken).ConfigureAwait(false);
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var _ = KeyHider.Hide(options);
        await foreach (var update in base.GetStreamingResponseAsync(Strip(messages), options, cancellationToken).ConfigureAwait(false))
        {
            yield return update;
        }
    }

    private static IEnumerable<ChatMessage> Strip(IEnumerable<ChatMessage> messages)
        => messages.Select(static message => message.WithoutHostContent());

    // The loop files the turn on the run's own options for the invoking client above; the
    // vendor below must never see it. Hidden in place around the inner call and restored
    // after: the options object belongs to this one run, and cloning it would drop vendor
    // fields this layer knows nothing about.
    private sealed class KeyHider : IDisposable
    {
        private readonly ChatOptions? _options;
        private readonly object? _held;
        private readonly bool _took;

        private KeyHider(ChatOptions? options)
        {
            _options = options;
            _took = options?.AdditionalProperties?.Remove(TurnInvocation.ArgumentsKey, out _held) ?? false;
        }

        public static KeyHider Hide(ChatOptions? options) => new(options);

        public void Dispose()
        {
            if (_took && _options?.AdditionalProperties is { } kept)
            {
                kept[TurnInvocation.ArgumentsKey] = _held;
            }
        }
    }
}
