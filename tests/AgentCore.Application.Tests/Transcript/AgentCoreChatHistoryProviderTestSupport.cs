using System.Text.Json;
using AgentCore.Application.Calls;
using AgentCore.Application.Calls.Memory;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Transcript;
using AgentCore.TestSupport;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Transcript;

/// <summary>What both halves of the provider's test suite share: the call id, the session state
/// key, and the plumbing to open a call and read its history back.</summary>
internal static class AgentCoreChatHistoryProviderTestSupport
{
    internal const string CallId = "call-1";

    /// <summary>
    /// The provider's MAF-default state key: its own type name. The transcript no longer files
    /// under it — or anywhere in the bag — and a change still renames what the collision checks
    /// compare, so it stays pinned here rather than inlined.
    /// </summary>
    internal const string StateKey = "AgentCoreChatHistoryProvider";

    /// <summary>
    /// Opens one call on a fresh session, the way <c>CallSession</c> does at call start: the row is
    /// made before any turn can append against it, exactly as <c>CallSession.OpenSessionAsync</c>
    /// makes it before it ever reaches this provider.
    /// </summary>
    internal static async Task<(AgentCoreChatHistoryProvider Provider, RecordingCallStore Store, StubSession Session)> NewCall()
    {
        var store = new RecordingCallStore();
        await store.CreateAsync(CallId, TestContext.Current.CancellationToken);
        var provider = new AgentCoreChatHistoryProvider(store);
        var session = new StubSession();
        provider.BeginCall(session, CallId, []);
        return (provider, store, session);
    }

    /// <summary>Writes one turn the way <c>CallSession</c> does: name the turn, then append it.</summary>
    internal static void AppendTurn(
        AgentCoreChatHistoryProvider provider,
        AgentSession session,
        int turnIndex,
        string said,
        string replied)
    {
        provider.BeginTurn(session, turnIndex);
        provider.AppendTurn(
            session,
            [new ChatMessage(ChatRole.User, said), new ChatMessage(ChatRole.Assistant, replied)]);
    }


    internal static async Task<IReadOnlyList<ChatMessage>> ProvideAsync(
        AgentCoreChatHistoryProvider provider, AgentSession session)
    {
#pragma warning disable MAAI001 // The context constructors are the framework's own experimental surface.
        var context = new ChatHistoryProvider.InvokingContext(StubAgent.Instance, session, []);
#pragma warning restore MAAI001
        var messages = await provider.InvokingAsync(context, TestContext.Current.CancellationToken);
        return [.. messages];
    }
}

/// <summary>
/// Holds one append open, so a barge-in can arrive while a turn is still writing. It keeps the
/// real store's ordering rule: a rewrite of a row that is not there yet changes nothing.
/// </summary>
internal sealed class BlockingCallStore() : DelegatingCallStore(new InMemoryCallStore())
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _block;

    public Task Entered => _entered.Task;

    public void BlockNextAppend() => _block = true;

    public void Release() => _release.TrySetResult();

    public override async ValueTask<IReadOnlyList<CallMessage>> AppendAsync(
        string callId,
        IReadOnlyList<CallMessageDraft> messages,
        CallSessionState? state = null,
        CancellationToken cancellationToken = default)
    {
        if (_block)
        {
            _block = false;
            _entered.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken);
        }

        return await Inner.AppendAsync(callId, messages, state, cancellationToken);
    }

    public override ValueTask RewriteAsync(
        string callId, string messageId, ChatMessage content, CancellationToken cancellationToken = default)
        => Inner.RewriteAsync(callId, messageId, content, cancellationToken);
}

internal sealed class StubSession : AgentSession;

/// <summary>Stands in for the agent the framework names on a context. Nothing here runs it.</summary>
internal sealed class StubAgent : AIAgent
{
    public static StubAgent Instance { get; } = new();

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(
        CancellationToken cancellationToken = default)
        => new(new StubSession());

    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement serializedState,
        JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    protected override Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}
