using AgentCore.Application.Calls.Memory;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tests.Runtime;
using AgentCore.Application.Transcript;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Transcript;

/// <summary>
/// A call outlives the session that started it.
/// </summary>
/// <remarks>
/// A session is held in memory and a call is not: the host restarts, the session expires, and the
/// same call arrives again on a new one. Until this file, that second session started at zero on
/// every counter it owns — an empty history, ordinal 0, turn 0 — so the caller met an agent with no
/// memory of them, and its first append collided with the rows the first session had already
/// written. Store 1 is what the second session is missing, and store 1 is where it is read from.
/// </remarks>
public sealed class CallSessionResumeTests
{
    private const string OneAgentYaml = """
        apiVersion: agentcore/v1
        name: resume-check
        agents:
          items:
            - { id: only, instructions: "greet the caller" }
        """;

    [Fact]
    public async Task ASecondSessionOfOneCall_SendsTheFirstTurnToTheModel()
    {
        InMemoryCallStore store = new();
        var callId = await FirstTurnAsync(store, "my name is Dana", "Hello Dana");

        using ScriptedChatClient reply = new("Dana");
        using RequestCapturingChatClient capture = new(reply);
        var resumed = CreateSession(OneAgentYaml, capture, store, callId);

        await resumed.RunTurnAsync("what is my name?", TestContext.Current.CancellationToken);

        Assert.NotEmpty(capture.Requests);
        Assert.Contains(
            capture.Requests[^1],
            message => message.Text.Contains("my name is Dana", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASecondSessionOfOneCall_KeepsEveryWordTheFirstOneWrote()
    {
        InMemoryCallStore store = new();
        var callId = await FirstTurnAsync(store, "my name is Dana", "Hello Dana");

        await SecondTurnAsync(store, callId, "what is my name?", "Dana");

        var rows = await store.ReadAsync(callId, TestContext.Current.CancellationToken);

        // Two turns, each writing what the caller said and what it heard. A resumed session that
        // restarted its ordinals would overwrite the first pair rather than follow them.
        Assert.Equal(4, rows.Count);
        Assert.Equal([0, 1, 2, 3], rows.Select(row => row.Ordinal));
    }

    [Fact]
    public async Task ASecondSessionOfOneCall_CountsItsTurnAsTheNextOne()
    {
        InMemoryCallStore store = new();
        var callId = await FirstTurnAsync(store, "my name is Dana", "Hello Dana");

        await SecondTurnAsync(store, callId, "what is my name?", "Dana");

        var rows = await store.ReadAsync(callId, TestContext.Current.CancellationToken);

        // The turn index is the join to the audit chain, so a second turn numbered 0 would claim the
        // first turn's events as its own.
        Assert.Equal([0, 0, 1, 1], rows.Select(row => row.TurnIndex));
    }

    [Fact]
    public async Task ASessionOfACallWithNoWords_StartsAtTheBeginning()
    {
        InMemoryCallStore store = new();
        await store.CreateAsync("empty-call", TestContext.Current.CancellationToken);

        await SecondTurnAsync(store, "empty-call", "hello", "hi there");

        var rows = await store.ReadAsync("empty-call", TestContext.Current.CancellationToken);

        Assert.Equal([0, 1], rows.Select(row => row.Ordinal));
        Assert.Equal([0, 0], rows.Select(row => row.TurnIndex));
    }

    [Fact]
    public async Task ASecondSessionOfOneCall_WhoseWordsCannotBeRead_RefusesTheTurn()
    {
        InMemoryCallStore backing = new();
        UnreadableCallStore store = new(backing);
        var callId = await FirstTurnAsync(store, "my name is Dana", "Hello Dana");

        using ScriptedChatClient reply = new("Dana");
        var resumed = CreateSession(OneAgentYaml, reply, store, callId);

        // A read that answered empty would run the turn with no memory of the caller. Meeting a
        // stranger is worse than meeting an error, so the turn does not run at all.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => resumed.RunTurnAsync("what is my name?", TestContext.Current.CancellationToken));
    }

    /// <summary>A store 1 that takes words and will not give them back.</summary>
    private sealed class UnreadableCallStore(ICallStore inner) : DelegatingCallStore(inner)
    {
        public override ValueTask<IReadOnlyList<CallMessage>> ReadAsync(
            string callId, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("store 1 will not answer a read.");
    }

    private static async Task<string> FirstTurnAsync(
        ICallStore store, string said, string heard)
    {
        using ScriptedChatClient reply = new(heard);
        var session = CreateSession(OneAgentYaml, reply, store);

        await session.RunTurnAsync(said, TestContext.Current.CancellationToken);
        await session.FlushTranscriptAsync();

        return session.CallId;
    }

    private static async Task SecondTurnAsync(
        ICallStore store, string callId, string said, string heard)
    {
        using ScriptedChatClient reply = new(heard);
        var session = CreateSession(OneAgentYaml, reply, store, callId);

        await session.RunTurnAsync(said, TestContext.Current.CancellationToken);
        await session.FlushTranscriptAsync();
    }

    private static CallSession CreateSession(
        string yaml, IChatClient reply, ICallStore store, string? callId = null)
    {
        var document = ConfigurationLoader.LoadYaml(yaml);
        var chatClients = new FakeChatClientFactory(reply);
        var compiled = ConfigurationCompiler.Compile(
            document,
            new AgentCompilationContext(chatClients)
            {
                CallStore = store,
                Tools = TestToolRegistry.From(document, null, TestContext.Current.CancellationToken),
            });

        var factory = new CallSessionFactory(
            compiled,
            new GuardEvaluator(compiled.Configuration.Guards),
            extractor: null);

        return factory.Create(callId);
    }
}
