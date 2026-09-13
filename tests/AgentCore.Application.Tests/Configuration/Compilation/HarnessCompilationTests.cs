using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tests.Runtime;
using Microsoft.Agents.AI;
using Xunit;

namespace AgentCore.Application.Tests.Configuration.Compilation;

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

    [Fact]
    public void Compile_TodosAndModeTrue_HarnessStateKeysIsTheUnionOfBoth()
    {
        var compiled = ConfigurationCompiler.Compile(
            new AgentCoreConfiguration
            {
                ApiVersion = AgentCoreConfiguration.SupportedApiVersion,
                Name = "harness-keys",
                Agents = new AgentsConfiguration
                {
                    Items = [new AgentConfiguration { Id = "only", Todos = true, Mode = true }],
                },
            },
            new AgentCompilationContext(new FakeChatClientFactory(new SequencedChatClient("hello there."))));

        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal)
            {
                new TodoProvider().StateKeys[0],
                new AgentModeProvider().StateKeys[0],
            },
            compiled.HarnessStateKeys);
    }

    [Fact]
    public void Compile_NeitherTodosNorMode_HarnessStateKeysIsEmpty()
    {
        var compiled = ConfigurationCompiler.Compile(
            new AgentCoreConfiguration
            {
                ApiVersion = AgentCoreConfiguration.SupportedApiVersion,
                Name = "harness-no-keys",
                Agents = new AgentsConfiguration
                {
                    Items = [new AgentConfiguration { Id = "only" }],
                },
            },
            new AgentCompilationContext(new FakeChatClientFactory(new SequencedChatClient("hello there."))));

        Assert.Empty(compiled.HarnessStateKeys);
    }

    private const string TwoAgentsOnlyOneWithTodosYaml =
        """
        apiVersion: agentcore/v1
        name: harness-policy-keys
        guards:
          always: { ">=": [ { var: turnIndex }, 0 ] }
        agents:
          items:
            - { id: todoer, instructions: "track todos", todos: true }
            - { id: plain, instructions: "just talk" }
        policy:
          initial: working
          stages:
            - { id: working, agent: todoer, to: [ { stage: done, when: always } ] }
            - { id: done, agent: plain, terminal: true }
        """;

    [Fact]
    public void Compile_TwoAgentsOnlyOneWithTodos_HarnessStateKeysHasTheTodoKey()
    {
        var compiled = ConfigurationCompiler.Compile(
            ConfigurationLoader.LoadYaml(TwoAgentsOnlyOneWithTodosYaml),
            new AgentCompilationContext(new FakeChatClientFactory(new SequencedChatClient("hello there."))));

        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal) { new TodoProvider().StateKeys[0] },
            compiled.HarnessStateKeys);
    }

    private const string ShellYaml =
        """
        apiVersion: agentcore/v1
        name: harness-shell
        agents:
          items:
            - { id: only, instructions: "run commands", shell: { kind: local } }
        """;

    private const string ShellBadPolicyYaml =
        """
        apiVersion: agentcore/v1
        name: harness-shell-bad-policy
        agents:
          items:
            - { id: only, instructions: "run commands", shell: { kind: local, policy: { deny: ["("] } } }
        """;

    [Fact]
    public void Compile_ShellWithNoWorkspaceRoot_FailsNamingTheShellPointer()
    {
        var document = ConfigurationLoader.LoadYaml(ShellYaml);

        var failure = Assert.Throws<ConfigurationLoadException>(() => ConfigurationCompiler.Compile(
            document,
            new AgentCompilationContext(new FakeChatClientFactory(new SequencedChatClient("hi")))));

        Assert.Equal("/agents/items/0/shell", failure.Pointer);
        Assert.Contains("options.UseWorkspace(", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_ShellWithABadPolicyRegex_FailsNamingTheShellPolicyPointerAndThePattern()
    {
        var document = ConfigurationLoader.LoadYaml(ShellBadPolicyYaml);

        var failure = Assert.Throws<ConfigurationLoadException>(() => ConfigurationCompiler.Compile(
            document,
            new AgentCompilationContext(new FakeChatClientFactory(new SequencedChatClient("hi")))
            {
                WorkspaceRoot = Path.Combine(Path.GetTempPath(), "agentcore-shell-" + Guid.NewGuid().ToString("N")),
            }));

        Assert.Equal("/agents/items/0/shell/policy", failure.Pointer);
        Assert.Contains("'('", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_ShellWithAValidBlockAndARoot_Compiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "agentcore-shell-" + Guid.NewGuid().ToString("N"));
        try
        {
            var compiled = ConfigurationCompiler.Compile(
                ConfigurationLoader.LoadYaml(ShellYaml),
                new AgentCompilationContext(new FakeChatClientFactory(new SequencedChatClient("hi")))
                {
                    WorkspaceRoot = root,
                });

            Assert.Single(compiled.Agents.Values);
            Assert.Empty(compiled.HarnessStateKeys);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
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
