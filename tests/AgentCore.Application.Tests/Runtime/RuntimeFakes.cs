using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tests.Runtime;

/// <summary>
/// A clock a test owns. The turn loop reads <c>callDurationSeconds</c> from a
/// <see cref="TimeProvider"/>, so no test needs a stopwatch.
/// </summary>
internal sealed class TestTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    /// <summary>Moves the clock forward.</summary>
    public void Advance(TimeSpan span) => _now = _now.Add(span);
}

/// <summary>
/// A deterministic offline model that answers one reply for each request, in order.
/// </summary>
/// <remarks>
/// The turn loop makes two model calls in one turn, the reply and the extractor, and they need
/// different answers. This client answers a list, and it holds the last answer once the list runs
/// out.
/// </remarks>
internal sealed class SequencedChatClient : IChatClient
{
    private readonly string[] _replies;
    private int _calls;

    public SequencedChatClient(params string[] replies) => _replies = replies;

    /// <summary>Gets the messages of each request, in call order.</summary>
    public List<List<ChatMessage>> Requests { get; } = [];

    /// <summary>Gets the options of each request, in call order. A ChatClientAgent sends its
    /// instructions here rather than as a message.</summary>
    public List<ChatOptions?> Options { get; } = [];

    /// <summary>Gets how many requests this client answered.</summary>
    public int Calls => Volatile.Read(ref _calls);

    /// <summary>Gets the text of the last user message of one request.</summary>
    public string LastUserText(int request)
    {
        lock (Requests)
        {
            return Requests[request].LastOrDefault(message => message.Role == ChatRole.User)?.Text ?? string.Empty;
        }
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var index = Interlocked.Increment(ref _calls) - 1;
        lock (Requests)
        {
            Requests.Add([.. messages]);
            Options.Add(options);
        }

        await Task.Yield();

        var responseId = Guid.NewGuid().ToString("N");
        yield return new ChatResponseUpdate(ChatRole.Assistant, _replies[Math.Min(index, _replies.Length - 1)])
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

/// <summary>
/// A model that yields lifecycle updates between its text fragments.
/// </summary>
/// <remarks>
/// Section 8.6 measured <c>AsAIAgent()</c>: 47 updates for 40 text fragments, and seven of them carry
/// no content. This client reproduces both empty shapes, an update with no content at all and an
/// update whose only content is empty text, so a test proves the seam drops them.
/// </remarks>
internal sealed class LifecycleChatClient : IChatClient
{
    private readonly string[] _fragments;

    public LifecycleChatClient(params string[] fragments) => _fragments = fragments;

    /// <summary>Gets how many updates this client yields, content and lifecycle together.</summary>
    public int Yielded { get; private set; }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        await Task.Yield();

        var responseId = Guid.NewGuid().ToString("N");
        Yielded = 0;

        foreach (var fragment in _fragments)
        {
            // A lifecycle event. It carries nothing at all.
            Yielded++;
            yield return new ChatResponseUpdate { ResponseId = responseId, MessageId = responseId };

            Yielded++;
            yield return new ChatResponseUpdate(ChatRole.Assistant, fragment)
            {
                ResponseId = responseId,
                MessageId = responseId,
            };

            // The other empty shape: one content, and it holds no text.
            Yielded++;
            yield return new ChatResponseUpdate(ChatRole.Assistant, string.Empty)
            {
                ResponseId = responseId,
                MessageId = responseId,
            };
        }
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

/// <summary>
/// A model that calls the first tool it is offered on every request, and never answers with text.
/// </summary>
/// <remarks>
/// <see cref="ToolCallingChatClient"/> calls one tool once, which keeps a healthy run finite. Section
/// 8.7 needs the other case: a tool that keeps failing. This client never stops calling, so the run
/// spends the error budget of <c>MaximumConsecutiveErrorsPerRequest</c> and the 4th failure throws.
/// </remarks>
internal sealed class LoopingToolCallingChatClient : IChatClient
{
    private int _calls;

    /// <summary>Gets how many requests this client answered.</summary>
    public int Calls => Volatile.Read(ref _calls);

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var index = Interlocked.Increment(ref _calls);
        await Task.Yield();

        var responseId = Guid.NewGuid().ToString("N");
        if (options?.Tools?.OfType<AIFunction>().FirstOrDefault() is not { } tool)
        {
            // The extractor is offered no tool, so it still answers.
            yield return new ChatResponseUpdate(ChatRole.Assistant, "{}")
            {
                ResponseId = responseId,
                MessageId = responseId,
            };
            yield break;
        }

        yield return new ChatResponseUpdate(
            ChatRole.Assistant,
            [new FunctionCallContent($"call_{index}", tool.Name, new Dictionary<string, object?>(StringComparer.Ordinal))])
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

/// <summary>
/// Builds one function for each declared tool, and every one of them throws.
/// </summary>
/// <remarks>
/// A <c>builtin</c> tool returns an error result rather than throwing, so this factory stands for the
/// case section 8.7 keeps for a defect: the function faults, the framework feeds the error back, and
/// the 4th consecutive fault throws out of the run.
/// </remarks>
internal sealed class ThrowingToolFactory : IAgentToolFactory
{
    /// <summary>The message every fault carries.</summary>
    public const string Message = "the tool is down.";

    private int _calls;

    /// <summary>Gets how many times a tool of this factory ran.</summary>
    public int Calls => Volatile.Read(ref _calls);

    public AITool? Create(ToolConfiguration tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        return AIFunctionFactory.Create(Fail, tool.Id, tool.Description ?? tool.Id);
    }

    private string Fail()
    {
        Interlocked.Increment(ref _calls);
        throw new InvalidOperationException(Message);
    }
}

/// <summary>
/// Hands one client to the reply model and another to the extractor model.
/// </summary>
/// <remarks>
/// <c>providers.llm[].as</c> names each model, and the document points <c>extractor.model</c> at one
/// of those names. This factory routes on that name, so a test scripts the two models apart.
/// </remarks>
internal sealed class RoutingChatClientFactory : IChatClientFactory
{
    private readonly Dictionary<string, IChatClient> _byName = new(StringComparer.Ordinal);
    private readonly IChatClient _fallback;

    public RoutingChatClientFactory(IChatClient fallback) => _fallback = fallback;

    /// <summary>Binds one client to one <c>as</c> name.</summary>
    public RoutingChatClientFactory Route(string name, IChatClient client)
    {
        _byName[name] = client;
        return this;
    }

    public IChatClient GetChatClient(ModelReference? model)
        => model is not null && _byName.TryGetValue(model.Ref, out var client) ? client : _fallback;
}

/// <summary>
/// Builds one function for each declared tool, and answers it with a fixed JSON document.
/// </summary>
/// <remarks>
/// The turn loop reads a tool result out of the finished turn and hands it to
/// <c>ToolStateWriter</c>. This factory gives it something to read without an HTTP adapter.
/// </remarks>
internal sealed class StubToolFactory : IAgentToolFactory
{
    private readonly string _result;
    private readonly bool _asText;

    /// <summary>Creates the factory.</summary>
    /// <param name="result">The JSON document each tool answers.</param>
    /// <param name="asText">
    /// Whether the tool answers the document as one string. A real tool answers either way, because a
    /// tool result has no declared shape.
    /// </param>
    public StubToolFactory(string result, bool asText = false)
    {
        _result = result;
        _asText = asText;
    }

    /// <summary>Gets the id of each tool this factory ran, in call order.</summary>
    public List<string> Called { get; } = [];

    public AITool? Create(ToolConfiguration tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        var id = tool.Id;
        var description = tool.Description ?? id;

        if (_asText)
        {
            return AIFunctionFactory.Create(
                () =>
                {
                    Record(id);
                    return _result;
                },
                id,
                description);
        }

        return AIFunctionFactory.Create(
            () =>
            {
                Record(id);
                using var document = JsonDocument.Parse(_result);
                return document.RootElement.Clone();
            },
            id,
            description);
    }

    private void Record(string id)
    {
        lock (Called)
        {
            Called.Add(id);
        }
    }
}
