using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Llm;
using AgentCore.Application.Ports;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tests.Secrets.Fakes;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Llm;

/// <summary>
/// The composite behind <c>UseChatClients</c>. It routes each <c>providers.llm[]</c> entry to the
/// adapter whose <see cref="IChatClientAdapter.Kind"/> matches, and it owns everything no vendor owns:
/// the <c>as</c> map, the default entry, the temperature wrapper, and the client cache.
/// </summary>
/// <remarks>
/// Every adapter here is a fake, so every test runs offline. The document names the vendor and the
/// host registers the adapters; these tests prove the document alone decides which adapter builds
/// which client.
/// </remarks>
public sealed class CompositeChatClientFactoryTests
{
    private const string TwoVendorsYaml =
        """
        apiVersion: agentcore/v1
        name: two-vendors
        agents:
          items:
            - { id: only, instructions: "I answer everything" }
        providers:
          call:   { kind: telnyx-relay }
          speech:
            stt: { kind: telnyx-relay }
            tts: { kind: telnyx-relay }
          llm:
            - { kind: openai, model: gpt-4.1-mini, as: reply }
            - { kind: anthropic, model: claude-sonnet-5, as: fill }
        """;

    private const string OneVendorYaml =
        """
        apiVersion: agentcore/v1
        name: one-vendor
        agents:
          items:
            - { id: only, instructions: "I answer everything" }
        providers:
          call:   { kind: telnyx-relay }
          speech:
            stt: { kind: telnyx-relay }
            tts: { kind: telnyx-relay }
          llm:
            - { kind: openai, model: gpt-4.1-mini, as: reply }
            - { kind: openai, model: gpt-5.4-nano, as: fill }
        """;

    private const string UpperCaseKindYaml =
        """
        apiVersion: agentcore/v1
        name: shouted-vendor
        agents:
          items:
            - { id: only, instructions: "I answer everything" }
        providers:
          call:   { kind: telnyx-relay }
          speech:
            stt: { kind: telnyx-relay }
            tts: { kind: telnyx-relay }
          llm:
            - { kind: OpenAI, model: gpt-4.1-mini, as: reply }
        """;

    private const string DuplicateAsYaml =
        """
        apiVersion: agentcore/v1
        name: twice-named
        agents:
          items:
            - { id: only, instructions: "I answer everything" }
        providers:
          call:   { kind: telnyx-relay }
          speech:
            stt: { kind: telnyx-relay }
            tts: { kind: telnyx-relay }
          llm:
            - { kind: openai, model: gpt-4.1-mini, as: reply }
            - { kind: openai, model: gpt-5.4-nano, as: reply }
        """;

    private const string NoModelYaml =
        """
        apiVersion: agentcore/v1
        name: no-model
        agents:
          items:
            - { id: only, instructions: "I answer everything" }
        """;

    // ---------------------------------------------------------------------------------------------
    // Routing: the document names the vendor, and the matching adapter builds the client.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task TwoKinds_RouteToTheTwoAdaptersTheHostRegistered()
    {
        FakeChatClientAdapter openai = new("openai");
        FakeChatClientAdapter anthropic = new("anthropic");

        using var factory = await Create(TwoVendorsYaml, openai, anthropic);

        Assert.Same(openai.ClientOf("reply"), factory.GetChatClient(new ModelReference { Ref = "reply" }));
        Assert.Same(anthropic.ClientOf("fill"), factory.GetChatClient(new ModelReference { Ref = "fill" }));
    }

    [Fact]
    public async Task AKindWrittenInAnotherCase_StillFindsItsAdapter()
    {
        FakeChatClientAdapter openai = new("openai");

        using var factory = await Create(UpperCaseKindYaml, openai);

        Assert.Same(openai.ClientOf("reply"), factory.GetChatClient(new ModelReference { Ref = "reply" }));
    }

    [Fact]
    public async Task EveryClient_IsBuiltWhileTheFactoryBuildsAndNeverAgain()
    {
        FakeChatClientAdapter openai = new("openai");

        using var factory = await Create(OneVendorYaml, openai);
        factory.GetChatClient(new ModelReference { Ref = "reply" });
        factory.GetChatClient(new ModelReference { Ref = "reply" });
        factory.GetChatClient(new ModelReference { Ref = "fill" });

        // One build for each 'as' name, at startup. A call costs no build.
        Assert.Equal(["reply", "fill"], openai.BuiltNames);
    }

    [Fact]
    public async Task TheAdapter_ReceivesTheResolverChainTheHostBound()
    {
        FakeChatClientAdapter openai = new("openai");
        MapSecretResolver resolver = new();

        using var factory = await Create(UpperCaseKindYaml, resolver, openai);

        Assert.Same(resolver, openai.LastSecrets);
    }

    // ---------------------------------------------------------------------------------------------
    // The 'as' map and the default entry, exactly as the one-vendor factory held them.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task AReferenceWithNoModel_TakesTheFirstDeclaredEntry()
    {
        FakeChatClientAdapter openai = new("openai");

        using var factory = await Create(OneVendorYaml, openai);

        Assert.Same(factory.GetChatClient(new ModelReference { Ref = "reply" }), factory.GetChatClient(null));
    }

    [Fact]
    public async Task AnUnknownAsName_FailsAndNamesWhatTheDocumentDoesDeclare()
    {
        using var factory = await Create(OneVendorYaml, new FakeChatClientAdapter("openai"));

        var failure = Assert.Throws<ConfigurationLoadException>(
            () => factory.GetChatClient(new ModelReference { Ref = "summarise" }));

        Assert.Contains("'summarise'", failure.Message, StringComparison.Ordinal);
        Assert.Contains("'reply'", failure.Message, StringComparison.Ordinal);
        Assert.Contains("'fill'", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADocumentWithNoModel_FailsAReferenceThatNamesNone()
    {
        using var factory = await Create(NoModelYaml, new FakeChatClientAdapter("openai"));

        var failure = Assert.Throws<ConfigurationLoadException>(() => factory.GetChatClient(null));

        Assert.Contains("declares no model", failure.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------
    // The temperature wrapper: the document keeps its setting, the vendor client stays one.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task ATemperature_ReachesTheCallWithoutASecondVendorClient()
    {
        FakeChatClientAdapter openai = new("openai");

        using var factory = await Create(OneVendorYaml, openai);
        var shaped = factory.GetChatClient(new ModelReference { Ref = "reply", Temperature = 0.2 });
        await shaped.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], null, TestContext.Current.CancellationToken);

        Assert.Equal(0.2f, openai.RecorderOf("reply").LastOptions?.Temperature);
        Assert.Equal(["reply", "fill"], openai.BuiltNames);
    }

    [Fact]
    public async Task OneTemperature_TakesOneWrapperHoweverOftenItIsAsked()
    {
        using var factory = await Create(OneVendorYaml, new FakeChatClientAdapter("openai"));

        var first = factory.GetChatClient(new ModelReference { Ref = "reply", Temperature = 0.2 });
        var second = factory.GetChatClient(new ModelReference { Ref = "reply", Temperature = 0.2 });

        Assert.Same(first, second);
        Assert.NotSame(first, factory.GetChatClient(new ModelReference { Ref = "reply" }));
    }

    // ---------------------------------------------------------------------------------------------
    // What it refuses, at startup and never on the first call.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task AKindNoAdapterServes_FailsTheBuildAndNamesTheRegisteredKinds()
    {
        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
            async () => await Create(TwoVendorsYaml, new FakeChatClientAdapter("openai")));

        var error = Assert.Single(failure.Errors);
        Assert.Equal("/providers/llm/1/kind", error.Pointer);
        Assert.Contains("anthropic", error.Message, StringComparison.Ordinal);
        Assert.Contains("'openai'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TwoAdaptersForOneKindFailTheStart()
    {
        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
            async () => await Create(
                OneVendorYaml,
                new FakeChatClientAdapter("openai"),
                new FakeChatClientAdapter("openai")));

        Assert.Contains("two adapters", failure.Message, StringComparison.Ordinal);
        Assert.Contains("providers.llm", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TwoEntriesWithOneAsName_FailTheBuild()
    {
        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
            async () => await Create(DuplicateAsYaml, new FakeChatClientAdapter("openai")));

        var error = Assert.Single(failure.Errors);
        Assert.Equal("/providers/llm/1/as", error.Pointer);
        Assert.Contains("'reply'", error.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------------
    private static ValueTask<CompositeChatClientFactory> Create(string yaml, params IChatClientAdapter[] adapters)
        => Create(yaml, null, adapters);

    private static ValueTask<CompositeChatClientFactory> Create(
        string yaml,
        ISecretResolverPort? secrets,
        params IChatClientAdapter[] adapters)
        => CompositeChatClientFactory.CreateAsync(
            ConfigurationLoader.LoadYaml(yaml),
            secrets,
            adapters,
            TestContext.Current.CancellationToken);
}
