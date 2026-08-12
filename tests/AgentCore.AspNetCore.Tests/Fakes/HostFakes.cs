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

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var index = Interlocked.Increment(ref _calls) - 1;
        var reply = _replies[Math.Min(index, _replies.Length - 1)];
        var responseId = Guid.NewGuid().ToString("N");

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
