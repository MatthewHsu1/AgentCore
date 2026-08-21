using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
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

    /// <summary>Gets the text of the system messages of one request, joined by a newline.</summary>
    public string SystemText(int request)
    {
        lock (Requests)
        {
            return string.Join(
                '\n',
                Requests[request].Where(message => message.Role == ChatRole.System).Select(message => message.Text));
        }
    }

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
/// Builds one function for each declared tool, and every one of them throws a fault the model
/// cannot answer.
/// </summary>
/// <remarks>
/// <para>
/// A <c>builtin</c> tool returns an error result rather than throwing, so this factory stands for
/// the case section 8.7 keeps for a defect: the fault is beyond the model, and the 4th consecutive
/// one throws out of the run and spends the framework's own budget.
/// </para>
/// <para>
/// Task 7a moved that classification off <see cref="AgentCore.Application.Tools.DeclaredTool"/> and
/// into <see cref="AuditingFunctionInvokingChatClient"/>, the framework's single choke point for
/// every tool call. This factory builds a bare <c>AIFunctionFactory</c> tool, which is not a
/// <see cref="AgentCore.Application.Tools.DeclaredTool"/> at all, so it is the one thing in this file
/// that still exercises the classification of a fault reaching that middleware with no tool kind in
/// front of it. The exception type is deliberately one <c>IsBeyondTheModel</c> classifies as beyond
/// the model — before the move that was true of every exception this factory could throw, because
/// nothing classified it either way; after the move, only this arm keeps the run finite, since an
/// answerable fault would now become an error result the model reads and
/// <see cref="LoopingToolCallingChatClient"/> would call the tool again forever.
/// </para>
/// </remarks>
internal sealed class ThrowingToolBuilder
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
        throw new TimeoutException(Message);
    }
}


/// <summary>
/// Builds one function for each declared tool, and answers it with a fixed JSON document.
/// </summary>
/// <remarks>
/// The turn loop reads a tool result out of the finished turn and hands it to
/// <c>ToolStateWriter</c>. This factory gives it something to read without an HTTP adapter.
/// </remarks>
internal sealed class StubToolBuilder
{
    private readonly string _result;
    private readonly bool _asText;

    /// <summary>Creates the factory.</summary>
    /// <param name="result">The JSON document each tool answers.</param>
    /// <param name="asText">
    /// Whether the tool answers the document as one string. A real tool answers either way, because a
    /// tool result has no declared shape.
    /// </param>
    public StubToolBuilder(string result, bool asText = false)
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

/// <summary>
/// A model that calls one tool by a name of the test's choosing, once, then answers with text.
/// </summary>
/// <remarks>
/// <see cref="ToolCallingChatClient"/> always calls a tool the document declares. This one calls
/// whatever name it is given, so a test can reproduce the model INVENTING a tool name — the
/// <c>NotFound</c> case, which the framework answers with a message and no exception, so it spends
/// none of the error budget and the turn goes on. Nothing recorded it before.
/// </remarks>
internal sealed class NamedToolCallingChatClient : IChatClient
{
    private readonly string _toolName;
    private readonly string _reply;
    private readonly int _callsPerTurn;
    private int _calls;

    /// <summary>Creates the client.</summary>
    /// <param name="toolName">The name the model calls. It need not be a name the document declares.</param>
    /// <param name="reply">What it says once the tool round is over.</param>
    /// <param name="callsPerTurn">
    /// How many calls to that one name it emits in a single assistant message. Two reproduces the
    /// parallel-call case, where the name alone no longer identifies the call.
    /// </param>
    public NamedToolCallingChatClient(string toolName, string reply, int callsPerTurn = 1)
    {
        _toolName = toolName;
        _reply = reply;
        _callsPerTurn = callsPerTurn;
    }

    /// <summary>Gets the call ids this client handed out, in the order it emitted them.</summary>
    public List<string> CallIds { get; } = [];

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        await Task.Yield();

        var responseId = Guid.NewGuid().ToString("N");
        var answered = messages.Any(message => message.Contents.Any(content => content is FunctionResultContent));

        // The extractor is offered no tool and must still answer, so a request with no tool at all
        // never opens a tool round.
        if (answered || options?.Tools is not { Count: > 0 })
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, _reply)
            {
                ResponseId = responseId,
                MessageId = responseId,
            };
            yield break;
        }

        var round = Interlocked.Increment(ref _calls);
        List<AIContent> calls = [];
        for (var index = 0; index < _callsPerTurn; index++)
        {
            // Every call of one message carries its own id, exactly as a vendor emits them. This is
            // the only thing that tells two calls to the same tool apart.
            var callId = $"call_{round}_{index}";
            lock (CallIds)
            {
                CallIds.Add(callId);
            }

            calls.Add(new FunctionCallContent(callId, _toolName, new Dictionary<string, object?>(StringComparer.Ordinal)));
        }

        yield return new ChatResponseUpdate(ChatRole.Assistant, calls)
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
/// Builds one REAL <see cref="DeclaredTool"/> for each declared tool, and every one of them throws a
/// fault the model cannot answer.
/// </summary>
/// <remarks>
/// <see cref="ThrowingToolBuilder"/> builds a bare <c>AIFunctionFactory</c> delegate, which throws
/// straight at the framework. This one goes through the base every shipped tool kind shares, so it
/// exercises the classification <c>AuditingFunctionInvokingChatClient</c> applies and not just the
/// framework's reaction to it. Section 8.7 row six is only reachable through a tool that lets a fault
/// out.
/// </remarks>
internal sealed class UnreachableEndpointToolBuilder
{
    /// <summary>The message every fault carries.</summary>
    public const string Message = "no such host";

    private int _calls;

    /// <summary>Gets how many times a tool of this factory ran.</summary>
    public int Calls => Volatile.Read(ref _calls);

    public AITool? Create(ToolConfiguration tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        return new UnreachableEndpointTool(tool, this);
    }

    private void Record() => Interlocked.Increment(ref _calls);

    private sealed class UnreachableEndpointTool : DeclaredTool
    {
        private readonly UnreachableEndpointToolBuilder _owner;

        public UnreachableEndpointTool(ToolConfiguration tool, UnreachableEndpointToolBuilder owner)
            : base(tool) => _owner = owner;

        protected override ValueTask<object?> CallAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            _owner.Record();
            throw new HttpRequestException(Message);
        }
    }
}

/// <summary>
/// Builds one REAL <see cref="DeclaredTool"/> for each declared tool, and every one of them throws a
/// fault the model CAN answer.
/// </summary>
/// <remarks>
/// The counterpart of <see cref="UnreachableEndpointToolBuilder"/>, and the half of section 8.7 that
/// must not regress: the tool answers with an error result, the model reads it, and the turn ends
/// with a spoken reply and no failure at all.
/// </remarks>
internal sealed class RefusedRequestToolBuilder
{
    /// <summary>The message every fault carries.</summary>
    public const string Message = "the order is already closed.";

    private int _calls;

    /// <summary>Gets how many times a tool of this factory ran.</summary>
    public int Calls => Volatile.Read(ref _calls);

    public AITool? Create(ToolConfiguration tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        return new RefusedRequestTool(tool, this);
    }

    private void Record() => Interlocked.Increment(ref _calls);

    private sealed class RefusedRequestTool : DeclaredTool
    {
        private readonly RefusedRequestToolBuilder _owner;

        public RefusedRequestTool(ToolConfiguration tool, RefusedRequestToolBuilder owner)
            : base(tool) => _owner = owner;

        protected override ValueTask<object?> CallAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            _owner.Record();
            throw new InvalidOperationException(Message);
        }
    }
}

/// <summary>
/// A model that throws instead of answering, on both run shapes.
/// </summary>
/// <remarks>
/// R1 reaches the turn loop through a tool that failed four times, and that path needs a tool, a
/// looping model, and four round trips to set up. The fault this client raises is the same fault as
/// far as everything above the chat pipeline is concerned, so a test of the fallback layer says what
/// it means in three lines.
/// </remarks>
internal sealed class ThrowingChatClient : IChatClient
{
    private readonly Exception _fault;

    public ThrowingChatClient(Exception fault) => _fault = fault;

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw _fault;

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw _fault;

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

/// <summary>A screen that records everything it was asked to show, in the order it was shown.</summary>
internal sealed class RecordingRenderPort : IRenderPort
{
    public List<(string Name, object Data)> Published { get; } = [];

    public void Publish(string name, object data) => Published.Add((name, data));
}

/// <summary>
/// A drawing model: it answers each request with one call to <c>present</c> carrying the next
/// scripted tree, and answers with plain text once the script runs out.
/// </summary>
/// <remarks>
/// Enough to drive a real <c>ChatClientAgent</c> end to end, including recovery: a tree the
/// validator rejects comes back to the agent as <c>present</c>'s error result, the agent asks again,
/// and this client hands it the next tree. The text answer is what ends the run.
/// </remarks>
internal sealed class PresentCallingChatClient : IChatClient
{
    private readonly string[] _trees;
    private int _calls;

    public PresentCallingChatClient(params string[] trees) => _trees = trees;

    /// <summary>Gets how many requests this client answered.</summary>
    public int Calls => Volatile.Read(ref _calls);

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var index = Interlocked.Increment(ref _calls) - 1;
        await Task.Yield();

        var responseId = Guid.NewGuid().ToString("N");

        if (index >= _trees.Length)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, "drawn.")
            {
                ResponseId = responseId,
                MessageId = responseId,
            };
            yield break;
        }

        yield return new ChatResponseUpdate(
            ChatRole.Assistant,
            [new FunctionCallContent(
                $"call_{index}",
                "present",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["tree"] = JsonSerializer.Deserialize<JsonElement>(_trees[index]),
                })])
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
