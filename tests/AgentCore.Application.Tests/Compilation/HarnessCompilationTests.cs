using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tests.Runtime;
using Microsoft.Agents.AI;
using Xunit;

namespace AgentCore.Application.Tests.Compilation;

/// <summary>
/// The <c>todos:</c> / <c>mode:</c> switches reaching a compiled agent's context providers, and the
/// tools they add reaching the model.
/// </summary>
public sealed class HarnessCompilationTests
{
    private static readonly string[] ExpectedTodoTools =
        ["todos_add", "todos_complete", "todos_remove", "todos_get_remaining", "todos_get_all"];

    private static readonly string[] ExpectedModeTools = ["mode_set", "mode_get"];


    [Fact]
    public void Compile_TodosTrue_GetsATodoProvider()
    {
        var providers = Providers(CompileOne(withTodos: true, withMode: false));

        Assert.Contains(providers, provider => provider is TodoProvider);
    }

    [Fact]
    public void Compile_ModeTrue_GetsAnAgentModeProvider()
    {
        var providers = Providers(CompileOne(withTodos: false, withMode: true));

        Assert.Contains(providers, provider => provider is AgentModeProvider);
    }

    [Fact]
    public void Compile_NeitherKey_GetsNeitherProvider()
    {
        var providers = Providers(CompileOne(withTodos: false, withMode: false));

        Assert.DoesNotContain(providers, provider => provider is TodoProvider);
        Assert.DoesNotContain(providers, provider => provider is AgentModeProvider);
    }

    [Fact]
    public async Task Compile_TodosTrue_ModelSeesTheFiveTodoTools()
    {
        using SequencedChatClient reply = new("hello there.");

        var compiled = ConfigurationCompiler.Compile(
            new AgentCoreConfiguration
            {
                ApiVersion = AgentCoreConfiguration.SupportedApiVersion,
                Name = "harness-todos",
                Agents = new AgentsConfiguration
                {
                    Items = [new AgentConfiguration { Id = "only", Todos = true }],
                },
            },
            new AgentCompilationContext(new FakeChatClientFactory(reply)));

        var agent = Assert.Single(compiled.Agents.Values);
        var token = TestContext.Current.CancellationToken;
        var session = await agent.CreateSessionAsync(token);

        await agent.RunAsync("hi", session, cancellationToken: token);

        var toolNames = reply.Options[^1]?.Tools?.Select(tool => tool.Name).ToArray() ?? [];

        Assert.Equal(ExpectedTodoTools, toolNames, StringComparer.Ordinal);
    }

    [Fact]
    public async Task Compile_ModeTrue_ModelSeesTheTwoModeTools()
    {
        using SequencedChatClient reply = new("hello there.");

        var compiled = ConfigurationCompiler.Compile(
            new AgentCoreConfiguration
            {
                ApiVersion = AgentCoreConfiguration.SupportedApiVersion,
                Name = "harness-mode",
                Agents = new AgentsConfiguration
                {
                    Items = [new AgentConfiguration { Id = "only", Mode = true }],
                },
            },
            new AgentCompilationContext(new FakeChatClientFactory(reply)));

        var agent = Assert.Single(compiled.Agents.Values);
        var token = TestContext.Current.CancellationToken;
        var session = await agent.CreateSessionAsync(token);

        await agent.RunAsync("hi", session, cancellationToken: token);

        var toolNames = reply.Options[^1]?.Tools?.Select(tool => tool.Name).ToArray() ?? [];

        Assert.Equal(ExpectedModeTools, toolNames, StringComparer.Ordinal);
    }

    private static AIAgent CompileOne(bool withTodos, bool withMode)
    {
        using SequencedChatClient reply = new("hello there.");

        var compiled = ConfigurationCompiler.Compile(
            new AgentCoreConfiguration
            {
                ApiVersion = AgentCoreConfiguration.SupportedApiVersion,
                Name = "harness-only",
                Agents = new AgentsConfiguration
                {
                    Items = [new AgentConfiguration { Id = "only", Todos = withTodos, Mode = withMode }],
                },
            },
            new AgentCompilationContext(new FakeChatClientFactory(reply)));

        return Assert.Single(compiled.Agents.Values);
    }

    private static IEnumerable<AIContextProvider> Providers(AIAgent agent)
    {
        var inner = agent.GetService<ChatClientAgent>();
        Assert.NotNull(inner);
        return inner.AIContextProviders ?? [];
    }
}
