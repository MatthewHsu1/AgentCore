using Microsoft.Extensions.AI;

namespace AgentCore.TestSupport;

/// <summary>
/// Wraps one <see cref="IChatClient"/> and records the messages of every request it forwarded,
/// unmodified, in call order.
/// </summary>
/// <remarks>
/// A production seam that is supposed to strip something before the provider sees it can only be
/// proven by reading what actually reached the client sitting where the provider would be — never by
/// reading the response, which a stub controls regardless of what was stripped.
/// </remarks>
public sealed class RequestCapturingChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    private readonly List<IReadOnlyList<ChatMessage>> _requests = [];

    /// <summary>Gets the messages of each request this client forwarded, in call order.</summary>
    public IReadOnlyList<IReadOnlyList<ChatMessage>> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Capture(messages);
        return base.GetResponseAsync(messages, options, cancellationToken);
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Capture(messages);
        return base.GetStreamingResponseAsync(messages, options, cancellationToken);
    }

    private void Capture(IEnumerable<ChatMessage> messages)
    {
        var snapshot = messages.ToList();
        lock (_requests)
        {
            _requests.Add(snapshot);
        }
    }
}
