using System.Runtime.CompilerServices;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.Domain.Knowledge;
using Microsoft.Extensions.AI;

namespace AgentCore.AspNetCore.Tests.Fakes;

/// <summary>
/// A deterministic offline model that answers one reply for each request, in order.
/// </summary>
/// <remarks>
/// The turn loop makes two model calls in one turn, the reply and the extractor, and they need
/// different answers. This client answers a list, and it holds the last answer once the list runs
/// out. The streaming path yields one update for each word, so a test can see the reply arrive in
/// pieces rather than in one block.
/// </remarks>
internal sealed class SequencedChatClient : IChatClient
{
    private readonly string[] _replies;
    private int _calls;

    public SequencedChatClient(params string[] replies) => _replies = replies;

    /// <summary>Gets how many requests this client answered.</summary>
    public int Calls => Volatile.Read(ref _calls);

    /// <summary>Gets the messages of the most recent request, oldest first, or null before any call.</summary>
    /// <remarks>
    /// A test reads this to prove which text actually reached the model — for example that an
    /// interim transcript never substitutes for the final one, rather than only counting how many
    /// times the model ran.
    /// </remarks>
    public IReadOnlyList<ChatMessage>? LastRequest { get; private set; }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var index = Interlocked.Increment(ref _calls) - 1;
        var reply = _replies[Math.Min(index, _replies.Length - 1)];
        var responseId = Guid.NewGuid().ToString("N");
        LastRequest = [.. messages];

        foreach (var fragment in Fragments(reply))
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, fragment)
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

    /// <summary>Cuts one reply into the pieces the streaming path yields.</summary>
    /// <remarks>
    /// An extractor answer is one JSON document and must not be cut, because the extractor parses the
    /// whole text. Everything else is speech, and speech arrives word by word.
    /// </remarks>
    private static IEnumerable<string> Fragments(string reply)
    {
        if (reply.StartsWith('{'))
        {
            return [reply];
        }

        return reply.Split(' ').Select((word, index) => index == 0 ? word : " " + word);
    }
}

/// <summary>
/// A deterministic offline model that streams one reply, and pauses after the first piece leaves.
/// </summary>
/// <remarks>
/// A barge-in test needs to send an interrupt frame while a reply is mid-flight, and that race is
/// unwinnable against a client that finishes streaming before the test can act. Gating on a
/// <see cref="TaskCompletionSource"/> instead of a delay means the pause is exact: the test learns
/// the moment the first piece left, and the client waits until the test says to go on.
/// </remarks>
internal sealed class BlockingChatClient : IChatClient
{
    private readonly string _reply;
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _calls;

    public BlockingChatClient(string reply) => _reply = reply;

    /// <summary>Gets how many requests this client answered.</summary>
    public int Calls => Volatile.Read(ref _calls);

    /// <summary>Waits until the first update of the reply has left the client.</summary>
    /// <returns>A task that completes once the first piece is on its way.</returns>
    public Task WaitUntilStreamingAsync() => _started.Task;

    /// <summary>Lets every update after the first flow.</summary>
    public void Release() => _released.TrySetResult();

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        Interlocked.Increment(ref _calls);
        var responseId = Guid.NewGuid().ToString("N");
        var fragments = Fragments(_reply).ToList();

        for (var index = 0; index < fragments.Count; index++)
        {
            if (index == 1)
            {
                // The first piece already left below. This is the gate a barge-in test opens.
                await _released.Task;
            }

            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, fragments[index])
            {
                ResponseId = responseId,
                MessageId = responseId,
            };

            if (index == 0)
            {
                // This line runs on the next pull, which only happens once the caller already has
                // the first update, so "started" here really does mean "left the client".
                _started.TrySetResult();
            }
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

    /// <summary>Cuts one reply into the pieces the streaming path yields.</summary>
    /// <remarks>
    /// An extractor answer is one JSON document and must not be cut, because the extractor parses the
    /// whole text. Everything else is speech, and speech arrives word by word.
    /// </remarks>
    private static IEnumerable<string> Fragments(string reply)
    {
        if (reply.StartsWith('{'))
        {
            return [reply];
        }

        return reply.Split(' ').Select((word, index) => index == 0 ? word : " " + word);
    }
}

/// <summary>
/// Streams a first reply that pauses after its first piece, then holds the second reply back
/// before it says a word.
/// </summary>
/// <remarks>
/// This is the shape a held prompt produces on a real call. Turn one pauses long enough for a
/// second final prompt to be held, then finishes streaming; <c>RunPendingPrompt</c> starts turn two
/// inside turn one's own <c>finally</c>; and the vendor is still speaking turn one while turn two
/// has produced nothing at all. No other fake here can hold the second turn apart from the first.
/// </remarks>
internal sealed class HeldPromptChatClient : IChatClient
{
    private readonly string _first;
    private readonly string _second;
    private readonly TaskCompletionSource _firstStreaming = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _firstGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _secondGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _calls;

    public HeldPromptChatClient(string first, string second)
    {
        _first = first;
        _second = second;
    }

    /// <summary>Gets a task that completes once the second turn's model call is in flight and blocked.</summary>
    public TaskCompletionSource SecondTurnStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Waits until the first piece of the first reply has left this client.</summary>
    public Task WaitUntilFirstTurnStreamingAsync() => _firstStreaming.Task;

    /// <summary>Lets the rest of the first reply flow.</summary>
    public void ReleaseFirstTurn() => _firstGate.TrySetResult();

    /// <summary>Lets the second reply flow.</summary>
    public void ReleaseSecondTurn() => _secondGate.TrySetResult();

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var index = Interlocked.Increment(ref _calls);
        var responseId = Guid.NewGuid().ToString("N");

        if (index > 1)
        {
            SecondTurnStarted.TrySetResult();

            // The token is deliberately not read here. A defect that cancelled this turn would
            // otherwise end it quietly and look like the pass this fake exists to disprove.
            await _secondGate.Task.ConfigureAwait(false);
        }

        var fragments = Fragments(index == 1 ? _first : _second).ToList();
        for (var piece = 0; piece < fragments.Count; piece++)
        {
            if (index == 1 && piece == 1)
            {
                await _firstGate.Task.ConfigureAwait(false);
            }

            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, fragments[piece])
            {
                ResponseId = responseId,
                MessageId = responseId,
            };

            if (index == 1 && piece == 0)
            {
                // This line runs on the next pull, so "streaming" here means the first piece has
                // really left the client and the connection has already queued it.
                _firstStreaming.TrySetResult();
            }
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

    /// <summary>Cuts one reply into the pieces the streaming path yields.</summary>
    private static IEnumerable<string> Fragments(string reply)
        => reply.Split(' ').Select((word, index) => index == 0 ? word : " " + word);
}

/// <summary>
/// Hands one client to the reply model and another to the extractor model.
/// </summary>
/// <remarks>
/// <c>providers.llm[].as</c> names each model, and the document points <c>extractor.model</c> at one
/// of those names. This factory routes on that name, so a test scripts the two models apart. No test
/// in this project reaches a network or needs an API key.
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
/// An offline knowledge adapter that answers both knowledge ports and holds nothing.
/// </summary>
/// <remarks>
/// The file store of section 7 answers both ports the same way, so this fake stands in for it and
/// this test project keeps its reference list short. A test that binds one port passes this object
/// as one argument and leaves the other unbound.
/// </remarks>
internal sealed class EmptyKnowledgeStore : IKnowledgeRetrievalPort, IDocumentStorePort
{
    public ValueTask<IReadOnlyList<KnowledgeChunk>> SearchAsync(
        string query,
        int limit,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IReadOnlyList<KnowledgeChunk>>([]);

    public ValueTask<KnowledgeDocument?> ReadAsync(string documentId, CancellationToken cancellationToken = default)
        => ValueTask.FromResult<KnowledgeDocument?>(null);
}

/// <summary>
/// A resolver over a map a test writes. It holds no file and no environment variable.
/// </summary>
internal sealed class MapSecretResolver : ISecretResolverPort
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    /// <summary>Adds one name and its value.</summary>
    public MapSecretResolver With(string name, string value)
    {
        _values[name] = value;
        return this;
    }

    public ValueTask<string?> TryResolveAsync(string name, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(_values.TryGetValue(name, out var value) ? value : null);
}
