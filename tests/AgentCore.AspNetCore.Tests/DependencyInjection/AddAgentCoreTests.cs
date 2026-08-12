using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Runtime;
using AgentCore.Application.Secrets;
using AgentCore.Application.Tools;
using AgentCore.AspNetCore.DependencyInjection;
using AgentCore.AspNetCore.Sessions;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.Infrastructure.Tools;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentCore.AspNetCore.Tests.DependencyInjection;

/// <summary>
/// The composition root. It loads, validates, resolves, compiles, and registers, in that order.
/// </summary>
/// <remarks>
/// A configuration defect stops the host here and never on the first call. Every test proves that by
/// calling AddAgentCore alone, with no request anywhere.
/// </remarks>
public sealed class AddAgentCoreTests
{
    private const string OneAgentYaml =
        """
        apiVersion: agentcore/v1
        name: composed
        agents:
          items:
            - { id: only, instructions: "I answer everything" }
        providers:
          llm:
            - { kind: openai, model: gpt-4.1-mini, as: reply }
        """;

    private const string BindingYaml =
        """
        apiVersion: agentcore/v1
        name: with-binding
        tools:
          - id: create_case
            kind: binding
            binds: CreateCase
            description: Open a service case for a human agent.
            parameters:
              type: object
              properties: { summary: { type: string } }
              required: [ summary ]
        agents:
          items:
            - { id: only, instructions: "I answer everything", tools: [ create_case ] }
        providers:
          llm:
            - { kind: openai, model: gpt-4.1-mini, as: reply }
        """;

    private const string SecretYaml =
        """
        apiVersion: agentcore/v1
        name: with-secret
        tools:
          - id: lookup_order
            kind: http
            description: Read one order by its identifier.
            parameters:
              type: object
              properties: { orderId: { type: string } }
              required: [ orderId ]
            request:
              method: GET
              url: "https://api.example.com/orders/{orderId}"
              headers: { Authorization: "Bearer ${secret:orders-api-key}" }
        agents:
          items:
            - { id: only, instructions: "I answer everything", tools: [ lookup_order ] }
        providers:
          llm:
            - { kind: openai, model: gpt-4.1-mini, as: reply }
        """;

    // A stage names a target that policy.stages does not declare, so check 2 fails the load.
    private const string BrokenYaml =
        """
        apiVersion: agentcore/v1
        name: broken
        agents:
          items:
            - { id: only, instructions: "I answer everything" }
        policy:
          initial: start
          stages:
            - { id: start, agent: only, to: [ { stage: nowhere } ] }
        providers:
          llm:
            - { kind: openai, model: gpt-4.1-mini, as: reply }
        """;

    // -------------------------------------------------------------------------------------------
    // What it registers.
    // -------------------------------------------------------------------------------------------
    [Fact]
    public void AddAgentCore_RegistersTheCompiledAgentAsAProcessSingleton()
    {
        using var provider = Build(OneAgentYaml);

        var first = provider.GetRequiredService<CompiledAgent>();
        var second = provider.GetRequiredService<CompiledAgent>();

        Assert.Same(first, second);
        Assert.Equal("composed", first.Name);

        // The registry compiled once, and every call shares that one result.
        Assert.Equal(1, provider.GetRequiredService<CompiledAgentRegistry>().CompileCount);
    }

    [Fact]
    public void AddAgentCore_RegistersOneSessionFactoryThatBuildsANewSessionForEachCall()
    {
        using var provider = Build(OneAgentYaml);
        var factory = provider.GetRequiredService<ICallSessionFactory>();

        Assert.Same(factory, provider.GetRequiredService<ICallSessionFactory>());

        // A CallSession belongs to one call, so it is not a singleton and the container holds none.
        var first = factory.Create();
        var second = factory.Create();
        Assert.NotSame(first, second);
        Assert.NotEqual(first.CallId, second.CallId);
    }

    [Fact]
    public void AddAgentCore_RegistersTheInMemorySessionStoreByDefault()
    {
        using var provider = Build(OneAgentYaml);

        var store = provider.GetRequiredService<ICallSessionStore>();

        Assert.IsType<InMemoryCallSessionStore>(store);
        Assert.Same(store, provider.GetRequiredService<ICallSessionStore>());
    }

    [Fact]
    public void AddAgentCore_KeepsASessionStoreTheHostRegisteredFirst()
    {
        CountingCallSessionStore mine = new();
        ServiceCollection services = new();
        services.AddSingleton<ICallSessionStore>(mine);
        Configure(services, OneAgentYaml, null);

        using var provider = services.BuildServiceProvider();

        // A distributed store replaces the default one, and the default steps aside.
        Assert.Same(mine, provider.GetRequiredService<ICallSessionStore>());
    }

    // -------------------------------------------------------------------------------------------
    // The seams the host binds.
    // -------------------------------------------------------------------------------------------
    [Fact]
    public void ABindingTool_ReachesTheDelegateTheHostRegistered()
    {
        using var provider = Build(
            BindingYaml,
            options => options.Bind("CreateCase", (_, _) => ValueTask.FromResult<object?>(new JsonObject())));

        var bindings = provider.GetRequiredService<ToolBindingRegistry>();

        Assert.True(bindings.Contains("CreateCase"));
        Assert.Equal(1, bindings.Count);
    }

    [Fact]
    public void ABindingToolWithNoDelegate_FailsTheStartAndNamesTheBinding()
    {
        var failure = Assert.Throws<ConfigurationLoadException>(() => Build(BindingYaml));

        Assert.Contains("CreateCase", failure.Message, StringComparison.Ordinal);
        Assert.Contains("did not register", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASecretReference_ResolvesOnceAtStartup()
    {
        using HttpClient client = new();
        MapSecretResolver resolver = new();
        resolver.With("orders-api-key", "a-value-no-message-repeats");

        using var provider = Build(
            SecretYaml,
            options =>
            {
                options.SecretResolver = resolver;
                options.AddToolFactory(startup => new HttpToolFactory(client, startup.Secrets));
            });

        var secrets = provider.GetRequiredService<ResolvedSecrets>();

        Assert.True(secrets.Contains("orders-api-key"));

        // A resolved set lands in a log line sooner or later, so it reports the count and never a value.
        Assert.DoesNotContain("a-value-no-message-repeats", secrets.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ASecretReferenceWithNoResolver_FailsTheStartAndNamesTheSecret()
    {
        using HttpClient client = new();

        var failure = Assert.Throws<SecretResolutionException>(() => Build(
            SecretYaml,
            options => options.AddToolFactory(startup => new HttpToolFactory(client, startup.Secrets))));

        Assert.Equal("orders-api-key", failure.SecretName);
    }

    // -------------------------------------------------------------------------------------------
    // What it refuses.
    // -------------------------------------------------------------------------------------------
    [Fact]
    public void ABadDocument_FailsTheStartAndNamesTheFault()
    {
        var failure = Assert.Throws<ConfigurationLoadException>(() => Build(BrokenYaml));

        var error = Assert.Single(failure.Errors);
        Assert.Equal(ConfigurationCheck.ReferenceResolution, error.Check);
        Assert.Equal("/policy/stages/0/to/0/stage", error.Pointer);
        Assert.Contains("'nowhere' is not declared", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADocumentThatDoesNotParse_FailsTheStart()
    {
        ServiceCollection services = new();

        var failure = Assert.Throws<ConfigurationLoadException>(() => services.AddAgentCore(options =>
        {
            options.ConfigurationPath = "no-such-extension.txt";
            options.UseChatClients(_ => new RoutingChatClientFactory(new SequencedChatClient("hello")));
        }));

        Assert.Equal(ConfigurationCheck.Syntax, failure.Check);
    }

    [Fact]
    public void NoDocumentAtAll_FailsTheStartAndSaysWhatToSet()
    {
        ServiceCollection services = new();

        var failure = Assert.Throws<InvalidOperationException>(() => services.AddAgentCore(
            options => options.UseChatClients(_ => new RoutingChatClientFactory(new SequencedChatClient("hello")))));

        Assert.Contains("names no document", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoDocuments_FailTheStart()
    {
        ServiceCollection services = new();

        var failure = Assert.Throws<InvalidOperationException>(() => services.AddAgentCore(options =>
        {
            options.Configuration = ConfigurationLoader.LoadYaml(OneAgentYaml);
            options.ConfigurationPath = "config/example.yaml";
            options.UseChatClients(_ => new RoutingChatClientFactory(new SequencedChatClient("hello")));
        }));

        Assert.Contains("names two documents", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NoChatClientAdapter_FailsTheStart()
    {
        ServiceCollection services = new();

        var failure = Assert.Throws<InvalidOperationException>(() => services.AddAgentCore(
            options => options.Configuration = ConfigurationLoader.LoadYaml(OneAgentYaml)));

        Assert.Contains("UseChatClients", failure.Message, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------------------------
    // Helpers.
    // -------------------------------------------------------------------------------------------
    private static ServiceProvider Build(string yaml, Action<AgentCoreOptions>? configure = null)
    {
        ServiceCollection services = new();
        Configure(services, yaml, configure);
        return services.BuildServiceProvider();
    }

    private static void Configure(ServiceCollection services, string yaml, Action<AgentCoreOptions>? configure)
        => services.AddAgentCore(options =>
        {
            options.Configuration = ConfigurationLoader.LoadYaml(yaml);
            options.UseChatClients(_ => new RoutingChatClientFactory(new SequencedChatClient("hello")));
            configure?.Invoke(options);
        });

    /// <summary>A store a host registers in place of the default one.</summary>
    private sealed class CountingCallSessionStore : ICallSessionStore
    {
        public ValueTask<CallSession?> TryGetAsync(string sessionId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<CallSession?>(null);

        public ValueTask AddAsync(CallSession session, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask<bool> RemoveAsync(string sessionId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(false);
    }
}
