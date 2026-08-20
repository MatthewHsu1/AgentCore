using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tests.Fakes;

/// <summary>
/// A deterministic offline model that calls the first tool it is offered, once, then answers.
/// </summary>
/// <remarks>
/// <para>
/// A delegating agent needs a model that actually emits a tool call, and
/// <see cref="ScriptedChatClient"/> only emits text. This client emits one
/// <see cref="FunctionCallContent"/> when the request carries a tool and the transcript holds no
/// tool result yet. Every other request answers with text, so the inner agent of a
/// <c>kind: agent</c> tool, which is offered no tool, always replies.
/// </para>
/// <para>
/// The one call and the "no result yet" test together keep the run finite. Nothing here depends on
/// the 40-iteration cap of section 8.7.
/// </para>
/// </remarks>
internal sealed class ToolCallingChatClient : IChatClient
{
    private const string CallId = "call_1";

    private readonly string _reply;
    private readonly Dictionary<string, object?> _arguments;
    private int _calls;

    public ToolCallingChatClient(string reply, Dictionary<string, object?>? arguments = null)
    {
        _reply = reply;
        _arguments = arguments ?? new Dictionary<string, object?>(StringComparer.Ordinal);
    }

    /// <summary>Gets the text of the last user message of each request, in call order.</summary>
    public List<string> Prompts { get; } = [];

    /// <summary>Gets every tool result this client read back, in call order.</summary>
    public List<string> ToolResults { get; } = [];

    /// <summary>Gets the name of each tool this client asked to call.</summary>
    public List<string> Called { get; } = [];

    /// <summary>Gets every function this client was offered, in call order. This is what the model reads.</summary>
    public List<AIFunction> Offered { get; } = [];

    /// <summary>Gets how many requests this client answered.</summary>
    public int Calls => Volatile.Read(ref _calls);

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        Interlocked.Increment(ref _calls);
        await Task.Yield();

        var transcript = messages.ToList();
        var answered = false;

        lock (Prompts)
        {
            foreach (var message in transcript)
            {
                foreach (var content in message.Contents)
                {
                    if (content is FunctionResultContent result)
                    {
                        answered = true;
                        ToolResults.Add(result.Result?.ToString() ?? string.Empty);
                    }
                }
            }

            var prompt = transcript.LastOrDefault(message => message.Role == ChatRole.User);
            Prompts.Add(prompt?.Text ?? string.Empty);
        }

        var responseId = Guid.NewGuid().ToString("N");
        var functions = options?.Tools?.OfType<AIFunction>().ToList() ?? [];
        var tool = functions.FirstOrDefault();

        lock (Prompts)
        {
            Offered.AddRange(functions);
        }

        if (tool is not null && !answered)
        {
            lock (Prompts)
            {
                Called.Add(tool.Name);
            }

            yield return new ChatResponseUpdate(
                ChatRole.Assistant,
                [new FunctionCallContent(CallId, tool.Name, _arguments)])
            {
                ResponseId = responseId,
                MessageId = responseId,
            };

            yield break;
        }

        yield return new ChatResponseUpdate(ChatRole.Assistant, _reply)
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
