using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tests.Fakes;

/// <summary>One request a <see cref="ShellScriptedClient"/> answered: what it saw, and what it was offered.</summary>
internal sealed record RecordedRequest(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options);

/// <summary>
/// A deterministic offline model that calls the shell tool once per queued command, feeding the
/// prior tool result back into <see cref="ToolResults"/> before asking for the next, then answers
/// "done" once the queue is empty.
/// </summary>
internal sealed class ShellScriptedClient : IChatClient
{
    private readonly string _toolName;
    private readonly Queue<string> _commands;

    public ShellScriptedClient(string toolName, params string[] commands)
    {
        _toolName = toolName;
        _commands = new(commands);
    }

    public List<RecordedRequest> Requests { get; } = [];

    public List<string> ToolResults { get; } = [];

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        await Task.Yield();

        var transcript = messages.ToList();
        Requests.Add(new RecordedRequest(transcript, options));
        var responseId = Guid.NewGuid().ToString("N");

        var lastResults = transcript.Count > 0
            ? transcript[^1].Contents.OfType<FunctionResultContent>().ToList()
            : [];

        foreach (var result in lastResults)
        {
            ToolResults.Add(result.Result?.ToString() ?? string.Empty);
        }

        if (_commands.Count > 0)
        {
            yield return new ChatResponseUpdate(
                ChatRole.Assistant,
                [new FunctionCallContent(
                    "call_" + Guid.NewGuid().ToString("N"),
                    _toolName,
                    new Dictionary<string, object?>(StringComparer.Ordinal) { ["command"] = _commands.Dequeue() })])
            {
                ResponseId = responseId,
                MessageId = responseId,
            };
            yield break;
        }

        yield return new ChatResponseUpdate(ChatRole.Assistant, "done")
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
        // Nothing to release.
    }
}
