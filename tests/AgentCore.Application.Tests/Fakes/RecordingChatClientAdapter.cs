using System.Runtime.CompilerServices;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tests.Fakes;

/// <summary>
/// An offline vendor adapter. It answers one <c>kind</c> and records what it was asked to build.
/// </summary>
internal sealed class RecordingChatClientAdapter : IChatClientAdapter
{
    private readonly List<string> _builtNames = [];
    private readonly Dictionary<string, RecordingChatClient> _clients = new(StringComparer.Ordinal);

    public RecordingChatClientAdapter(string kind) => Kind = kind;

    public string Kind { get; }

    /// <summary>Gets the <c>as</c> names this adapter built a client for, in build order.</summary>
    public IReadOnlyList<string> BuiltNames => _builtNames;

    /// <summary>Gets the resolver chain the last build received.</summary>
    public ISecretResolverPort? LastSecrets { get; private set; }

    /// <summary>Gets the client built for one <c>as</c> name.</summary>
    public IChatClient ClientOf(string asName) => _clients[asName];

    /// <summary>Gets the same client, typed so a test reads what a call carried.</summary>
    public RecordingChatClient RecorderOf(string asName) => _clients[asName];

    public ValueTask<IChatClient> CreateClientAsync(
        LlmProviderConfiguration entry,
        ISecretResolverPort? secrets,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        _builtNames.Add(entry.As);
        LastSecrets = secrets;

        RecordingChatClient client = new();
        _clients[entry.As] = client;
        return ValueTask.FromResult<IChatClient>(client);
    }
}

/// <summary>
/// A chat client that keeps the options of its last call, so a test reads what the seam set.
/// </summary>
internal sealed class RecordingChatClient : IChatClient
{
    /// <summary>Gets the options of the last call, or <see langword="null"/> before one.</summary>
    public ChatOptions? LastOptions { get; private set; }

    /// <summary>Gets how many times <see cref="Dispose"/> ran, so a test can catch a double dispose.</summary>
    public int DisposeCount { get; private set; }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        LastOptions = options;
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        LastOptions = options;
        await Task.CompletedTask.ConfigureAwait(false);
        yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    }

    public void Dispose()
    {
        DisposeCount++;
    }
}
