using System.Runtime.CompilerServices;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Conversation;
using AgentCore.Application.Conversation.Memory;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Runtime.Compaction;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tests.Runtime;
using AgentCore.Application.Transcript;
using AgentCore.TestSupport;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentCore.Application.Tests.Transcript;

/// <summary>Answers each request in turn, keeps what it was asked, and runs a hook before answering.</summary>
/// <remarks>Buffered path only, like <see cref="RequestRecordingChatClient"/>.</remarks>
internal sealed class CapturingChatClient : IChatClient
{
    private readonly Func<int, Task> _beforeReply;

    private readonly string[] _replies;

    private int _calls;

    public CapturingChatClient(Func<int, Task> beforeReply, params string[] replies)
    {
        _beforeReply = beforeReply;
        _replies = replies;
    }

    /// <summary>Gets every request this client answered, one role-prefixed line per message: its text, or its tool results.</summary>
    public List<List<string>> Requests { get; } = [];

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var callIndex = _calls++;
        Requests.Add([.. messages.Select(message => $"{message.Role}:{Words(message)}")]);

        await _beforeReply(callIndex).ConfigureAwait(false);

        return new ChatResponse(new ChatMessage(ChatRole.Assistant, _replies[callIndex]));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("These facts drive the buffered path only.");

    private static string Words(ChatMessage message)
        => message.Text.Length > 0
            ? message.Text
            : string.Join("|", message.Contents.OfType<FunctionResultContent>().Select(result => result.Result?.ToString()));

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
