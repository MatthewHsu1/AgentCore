using AgentCore.Application.Calls;
using AgentCore.Application.Calls.Memory;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tests.Runtime;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Transcript;

/// <summary>A call's row exists before its first word does.</summary>
public sealed class CallSessionCallRowTests
{
    private const string OneAgentYaml = """
        apiVersion: agentcore/v1
        name: call-row-check
        agents:
          items:
            - { id: only, instructions: "greet the caller" }
        """;

    /// <summary>A store 0 that is down: it takes no row, so no word may follow.</summary>
    private sealed class RefusingCreate(ICallStore inner) : DelegatingCallStore(inner)
    {
        public override ValueTask<CallRecord> CreateAsync(
            string callId, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("store 0 is down.");
    }

    [Fact]
    public async Task ATurn_CreatesTheCallRow_BeforeItWritesAnyWord()
    {
        // Arrange
        InMemoryCallStore store = new();
        using ScriptedChatClient reply = new("hello");
        var session = CreateSession(OneAgentYaml, reply, store);

        // Act
        await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

        // Assert
        await session.FlushTranscriptAsync();
        Assert.NotNull(await store.GetAsync(session.CallId, TestContext.Current.CancellationToken));
        Assert.NotEmpty(await store.ReadAsync(session.CallId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ATurn_WhenStoreZeroRefusesTheRow_FailsAndWritesNoWords()
    {
        // Arrange
        InMemoryCallStore inner = new();
        RefusingCreate store = new(inner);
        using ScriptedChatClient reply = new("hello");
        var session = CreateSession(OneAgentYaml, reply, store);

        // Act
        var fault = await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.RunTurnAsync("hi", TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal("store 0 is down.", fault.Message);
        Assert.Empty(await inner.ReadAsync(session.CallId, TestContext.Current.CancellationToken));
    }

    private static CallSession CreateSession(string yaml, IChatClient reply, ICallStore store)
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

        return factory.Create();
    }
}
