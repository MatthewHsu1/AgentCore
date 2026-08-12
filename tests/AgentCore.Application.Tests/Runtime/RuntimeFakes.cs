using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Schema;
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
