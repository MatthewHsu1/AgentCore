using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tests.Fakes;

/// <summary>Answers each request in turn, and keeps what it was asked.</summary>
/// <remarks>
/// It drives the buffered path only. <see cref="Requests"/> is what a fact about the prompt reads:
/// one role-prefixed line per message, in the order the run sent them.
/// </remarks>
internal sealed class RequestRecordingChatClient : IChatClient
{
    private readonly string[] _replies;

    public RequestRecordingChatClient(params string[] replies) => _replies = replies;

    /// <summary>Gets every request this client answered, one role-prefixed line per message.</summary>
    public List<List<string>> Requests { get; } = [];

    /// <summary>Gets the instructions of each request, in call order. A per-invocation context lands here.</summary>
    public List<string?> Instructions { get; } = [];

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        Requests.Add([.. messages.Select(message => $"{message.Role}:{message.Text}")]);
        Instructions.Add(options?.Instructions);
        return Task.FromResult(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, _replies[Requests.Count - 1])));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("These facts drive the buffered path only.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
