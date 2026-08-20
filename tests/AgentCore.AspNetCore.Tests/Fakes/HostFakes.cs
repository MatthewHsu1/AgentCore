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
internal sealed class FragmentingChatClient : IChatClient
{
    private readonly string[] _replies;
    private int _calls;

    public FragmentingChatClient(params string[] replies) => _replies = replies;

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

    /// <summary>Gets whether the second turn opens with one update the relay can never speak.</summary>
    /// <remarks>
    /// <para>
    /// <c>CallSession</c> calls a run audible at its first piece of <i>content</i>, and content is not
    /// the same thing as a word. A tool call, a tool result, and a line of reasoning all count, and
    /// none of them carries text, so none of them ever reaches the vendor as a <c>text</c> frame. Set
    /// this, and turn two yields exactly one such update — a <see cref="TextReasoningContent"/>, the
    /// shape a reasoning model really does stream before its answer — and only then blocks. That is
    /// the one window in which the core believes turn two is audible while the connection's own
    /// <c>_spokenTurnId</c> still names turn one, which is the turn Telnyx is still speaking.
    /// </para>
    /// <para>
    /// Left <see langword="false"/>, turn two says nothing whatever before it blocks, which is the
    /// window <c>AnInterruptWhileAHeldTurnHasSaidNothing_LeavesThatTurnFreeToSpeak</c> already covers
    /// and which both sides of the seam already agree about on their own.
    /// </para>
    /// </remarks>
    public bool SecondTurnOpensWithUnspokenContent { get; init; }

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
            if (SecondTurnOpensWithUnspokenContent)
            {
                // Yielded before SecondTurnStarted is set, and the line after a yield only runs once
                // the consumer comes back for the next update. Waiting on that signal therefore
                // proves this update has already travelled the whole way through CallSession — which
                // is where it raises the audible flag this fake exists to raise — and through the
                // connection's own update loop, which reads no text on it and so leaves
                // _spokenTurnId naming turn one.
                yield return new ChatResponseUpdate(ChatRole.Assistant, [new TextReasoningContent("thinking")])
                {
                    ResponseId = responseId,
                    MessageId = responseId,
                };
            }

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

    public ValueTask<DocumentListing> ListAsync(string? pattern = null, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new DocumentListing { DocumentIds = [], Truncated = false });

    public ValueTask<GrepResult> GrepAsync(
        string pattern,
        string? glob = null,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new GrepResult { Matches = [], Truncated = false });
}

/// <summary>
/// An offline knowledge vendor. The document names it by its kind, exactly as it names a model.
/// </summary>
/// <remarks>
/// It builds one <see cref="RecordingKnowledgeStore"/> and hands the same object to both ports, so a
/// test reads which store a built-in tool actually reached. Which halves it serves is settable,
/// because the Zilliz connector of section 7 ranks and reads nothing.
/// </remarks>
internal sealed class FakeKnowledgeStoreAdapter : IKnowledgeStoreAdapter
{
    public FakeKnowledgeStoreAdapter(string kind) => Kind = kind;

    public string Kind { get; }

    public bool CanServeSearch { get; init; } = true;

    public bool CanServeDocuments { get; init; } = true;

    /// <summary>Gets the one store this vendor opened.</summary>
    public RecordingKnowledgeStore Store { get; } = new();

    /// <summary>Gets how many times the registry asked this vendor to rank.</summary>
    public int SearchBuilds { get; private set; }

    /// <summary>Gets how many times the registry asked this vendor to read.</summary>
    public int DocumentBuilds { get; private set; }

    public ValueTask<IKnowledgeRetrievalPort> CreateSearchAsync(
        KnowledgeProviderConfiguration entry,
        ISecretResolverPort? secrets,
        CancellationToken cancellationToken = default)
    {
        if (!CanServeSearch)
        {
            throw new NotSupportedException($"the '{Kind}' adapter does not rank.");
        }

        SearchBuilds++;
        return ValueTask.FromResult<IKnowledgeRetrievalPort>(Store);
    }

    public ValueTask<IDocumentStorePort> CreateDocumentsAsync(
        KnowledgeProviderConfiguration entry,
        ISecretResolverPort? secrets,
        CancellationToken cancellationToken = default)
    {
        if (!CanServeDocuments)
        {
            throw new NotSupportedException($"the '{Kind}' adapter does not read.");
        }

        DocumentBuilds++;
        return ValueTask.FromResult<IDocumentStorePort>(Store);
    }
}

/// <summary>
/// A knowledge store that answers nothing and remembers what it was asked.
/// </summary>
/// <remarks>
/// A built-in tool holds its port privately, so a test proves which port it holds by calling the
/// tool and reading what arrived here.
/// </remarks>
internal sealed class RecordingKnowledgeStore : IKnowledgeRetrievalPort, IDocumentStorePort
{
    /// <summary>Gets every query knowledge.search sent here, in call order.</summary>
    public List<string> Queries { get; } = [];

    /// <summary>Gets every document id knowledge.read sent here, in call order.</summary>
    public List<string> Reads { get; } = [];

    public ValueTask<IReadOnlyList<KnowledgeChunk>> SearchAsync(
        string query,
        int limit,
        CancellationToken cancellationToken = default)
    {
        Queries.Add(query);
        return ValueTask.FromResult<IReadOnlyList<KnowledgeChunk>>([]);
    }

    public ValueTask<KnowledgeDocument?> ReadAsync(string documentId, CancellationToken cancellationToken = default)
    {
        Reads.Add(documentId);
        return ValueTask.FromResult<KnowledgeDocument?>(null);
    }

    public ValueTask<DocumentListing> ListAsync(string? pattern = null, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new DocumentListing { DocumentIds = [], Truncated = false });

    public ValueTask<GrepResult> GrepAsync(
        string pattern,
        string? glob = null,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(new GrepResult { Matches = [], Truncated = false });
}
