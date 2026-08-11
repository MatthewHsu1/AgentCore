using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tests.Fakes;

/// <summary>
/// A deterministic offline stand-in for a chat model. No socket, no key.
/// </summary>
/// <remarks>
/// The streaming path yields one update for each fragment. A test may hold the stream open after the
/// first fragment with <see cref="GateAfterFirstFragment"/>, which turns "does the seam stream?" into
/// a question a test answers without a stopwatch.
/// </remarks>
internal sealed class ScriptedChatClient : IChatClient
{
    private readonly string[] _fragments;
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _calls;

    public ScriptedChatClient(params string[] fragments) => _fragments = fragments;

    /// <summary>Gets the text the model produces, as one string.</summary>
    public string FullText => string.Concat(_fragments);

    /// <summary>Gets or sets whether the stream stops after the first fragment until the gate opens.</summary>
    public bool GateAfterFirstFragment { get; set; }

    /// <summary>Gets how many requests this client answered.</summary>
    public int Calls => Volatile.Read(ref _calls);

    /// <summary>Lets the rest of the fragments flow.</summary>
    public void OpenGate() => _gate.TrySetResult();

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        Interlocked.Increment(ref _calls);

        var responseId = Guid.NewGuid().ToString("N");

        for (var index = 0; index < _fragments.Length; index++)
        {
            if (index == 1 && GateAfterFirstFragment)
            {
                await _gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            yield return new ChatResponseUpdate(ChatRole.Assistant, _fragments[index])
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
