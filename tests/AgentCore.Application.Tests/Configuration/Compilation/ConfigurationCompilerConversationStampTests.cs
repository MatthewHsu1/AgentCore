using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Llm;
using AgentCore.Application.Runtime;
using AgentCore.Application.Tests.Fakes;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Configuration.Compilation;

/// <summary>
/// A compiled agent stamps the running turn's conversation id on every request the vendor sees,
/// under <see cref="ChatRequestProperties.ConversationId"/>. This proves the COMPILED chain, not
/// the stamp on its own.
/// </summary>
public sealed class ConfigurationCompilerConversationStampTests
{
    private const string Yaml =
        """
        apiVersion: agentcore/v1
        agents:
          items:
            - { id: only, model: { ref: reply }, instructions: "greet the caller" }
        entries:
          main:
            agent: only
        """;

    [Fact]
    public async Task ARunInsideATurn_CarriesTheConversationIdToTheVendor()
    {
        var turn = new TurnInvocation { ConversationId = "conversation-7", TurnIndex = 0, Stage = "" };
        OptionsCapturingChatClient model = new();
        var compiled = Compile(model);

        var session = await compiled.CreateSessionAsync(TestContext.Current.CancellationToken);
        await compiled.RunAsync(
            [new ChatMessage(ChatRole.User, "hi")],
            session,
            turn.RunOptions(),
            TestContext.Current.CancellationToken);

        Assert.True(model.Seen!.AdditionalProperties!.TryGetValue(ChatRequestProperties.ConversationId, out string? stamped));
        Assert.Equal("conversation-7", stamped);
    }

    [Fact]
    public async Task ARunOutsideATurn_StampsNothing()
    {
        OptionsCapturingChatClient model = new();
        var compiled = Compile(model);

        var session = await compiled.CreateSessionAsync(TestContext.Current.CancellationToken);
        await compiled.RunAsync([new ChatMessage(ChatRole.User, "hi")], session, cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(model.Seen!.AdditionalProperties?.ContainsKey(ChatRequestProperties.ConversationId) ?? false);
    }

    private static Microsoft.Agents.AI.AIAgent Compile(IChatClient model)
    {
        var document = ConfigurationLoader.LoadYaml(Yaml);
        return ConfigurationCompiler.CompileAll(document, new AgentCompilationContext(new FakeChatClientFactory(model)))["main"].Agent;
    }

    private sealed class OptionsCapturingChatClient : IChatClient
    {
        public ChatOptions? Seen { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Seen = options;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
    }
}
