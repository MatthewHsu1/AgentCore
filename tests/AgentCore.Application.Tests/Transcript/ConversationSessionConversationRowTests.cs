using AgentCore.Application.Conversation;
using AgentCore.Application.Conversation.Memory;
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

/// <summary>A conversation's row exists before its first word does.</summary>
public sealed class ConversationSessionConversationRowTests
{
    private const string OneAgentYaml = """
        apiVersion: agentcore/v1
        agents:
          items:
            - { id: only, instructions: "ok" }
        entries:
          main:
            agent: only
        """;

    /// <summary>A store 0 that is down: it takes no row, so no word may follow.</summary>
    private sealed class RefusingCreate(IConversationStore inner) : DelegatingConversationStore(inner)
    {
        public override ValueTask<ConversationRecord> CreateAsync(
            string conversationId, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("store 0 is down.");
    }

    [Fact]
    public async Task ATurn_CreatesTheConversationRow_BeforeItWritesAnyWord()
    {
        // Arrange
        InMemoryConversationStore store = new();
        using ScriptedChatClient reply = new("hello");
        var session = CreateSession(OneAgentYaml, reply, store);

        // Act
        await session.RunTurnAsync("hi", TestContext.Current.CancellationToken);

        // Assert
        await session.FlushTranscriptAsync();
        Assert.NotNull(await store.GetAsync(session.ConversationId, TestContext.Current.CancellationToken));
        Assert.NotEmpty(await store.ReadAsync(session.ConversationId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ATurn_WhenStoreZeroRefusesTheRow_FailsAndWritesNoWords()
    {
        // Arrange
        InMemoryConversationStore inner = new();
        RefusingCreate store = new(inner);
        using ScriptedChatClient reply = new("hello");
        var session = CreateSession(OneAgentYaml, reply, store);

        // Act
        var fault = await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.RunTurnAsync("hi", TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal("store 0 is down.", fault.Message);
        Assert.Empty(await inner.ReadAsync(session.ConversationId, TestContext.Current.CancellationToken));
    }

    private static ConversationSession CreateSession(string yaml, IChatClient reply, IConversationStore store)
    {
        var document = ConfigurationLoader.LoadYaml(yaml);
        var chatClients = new FakeChatClientFactory(reply);
        var compiled = ConfigurationCompiler.CompileAll(
            document,
            new AgentCompilationContext(chatClients)
            {
                ConversationStore = store,
                Tools = TestToolRegistry.From(document, null, TestContext.Current.CancellationToken),
            })["main"];

        var factory = new ConversationSessionFactory(
            compiled,
            new GuardEvaluator(compiled.Configuration.Guards),
            extractor: null);

        return factory.Create();
    }
}
