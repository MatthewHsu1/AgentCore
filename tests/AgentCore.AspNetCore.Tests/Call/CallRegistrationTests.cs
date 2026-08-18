using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.AspNetCore.DependencyInjection;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.AspNetCore.Vendors.TelnyxRelay;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Call;

/// <summary>
/// <c>providers.call</c> is selected while the host starts, and the pairing rule runs there too.
/// </summary>
/// <remarks>
/// <para>
/// Registering a vendor is what turns this seam on, exactly as it is for telemetry, knowledge,
/// moderation, and speech. Every test here calls <c>AddAgentCoreAsync</c> alone, with no route
/// mapped anywhere, because agreement between two document entries is a document fact and is true
/// or false whether or not anything is ever routed.
/// </para>
/// <para>
/// Every test runs offline against a fake model. There is no Telnyx account, no network call, and
/// no API key anywhere in this file.
/// </para>
/// </remarks>
public sealed class CallRegistrationTests
{
    /// <summary>A call transport that is a name and a wire fact, which is all the port asks for.</summary>
    private sealed class FakeCallAdapter(string kind, bool carriesText) : ICallAdapter
    {
        public string Kind { get; } = kind;

        public bool CarriesText { get; } = carriesText;
    }

    [Fact]
    public async Task AMatchingPairStarts()
    {
        using var provider = await BuildAsync(
            callKind: "telnyx-relay",
            speechKind: "telnyx-relay",
            new FakeCallAdapter("telnyx-relay", carriesText: true));

        Assert.NotNull(provider.GetService<IReadOnlyList<ICallAdapter>>());
    }

    [Fact]
    public async Task AMismatchedPairFailsTheStartEvenWithNoRouteMapped()
    {
        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
            async () => await BuildAsync(
                callKind: "telnyx-relay",
                speechKind: "deepgram",
                new FakeCallAdapter("telnyx-relay", carriesText: true)));

        Assert.Equal("/providers/speech/stt/kind", failure.Errors[0].Pointer);
    }

    [Fact]
    public async Task AHostThatRegistersNoCallAdapterIsNotAskedAnything()
    {
        // The seam is off, exactly as telemetry, knowledge, and moderation are when nothing is
        // registered for them. A contradictory document is not read, and the start succeeds.
        using var provider = await BuildAsync(callKind: "telnyx-relay", speechKind: "deepgram");

        Assert.Null(provider.GetService<IReadOnlyList<ICallAdapter>>());
    }

    [Fact]
    public async Task ADocumentNamingAnUnregisteredCallKindFailsTheStart()
    {
        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
            async () => await BuildAsync(
                callKind: "sip",
                speechKind: "sip",
                new FakeCallAdapter("telnyx-relay", carriesText: true)));

        Assert.Equal("/providers/call/kind", failure.Errors[0].Pointer);
    }

    // ---------------------------------------------------------------------------------------------
    // The two blocks are required by the schema, so a loaded document that writes a providers
    // section carries both. A configuration a host built in code passes through no schema at all,
    // and so does a loaded document that writes no providers section: the root requires only
    // apiVersion and name. Both routes reach the guard with a block missing, and both are refused
    // by a message that names the block rather than by a NullReferenceException.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task AConfigurationBuiltInCodeWithNoCallBlockFailsTheStart()
    {
        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
            async () => await BuildFromAsync(
                InCode(new ProvidersConfiguration
                {
                    Llm = OneModel,
                    Speech = new SpeechProviderConfiguration
                    {
                        Stt = new VendorProviderConfiguration { Kind = "telnyx-relay" },
                        Tts = new VendorProviderConfiguration { Kind = "telnyx-relay" },
                    },
                }),
                new FakeCallAdapter("telnyx-relay", carriesText: true)));

        Assert.Equal("/providers/call", failure.Errors[0].Pointer);
    }

    [Fact]
    public async Task AConfigurationBuiltInCodeWithNoSpeechBlockFailsTheStart()
    {
        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
            async () => await BuildFromAsync(
                InCode(new ProvidersConfiguration
                {
                    Llm = OneModel,
                    Call = new CallProviderConfiguration { Kind = "telnyx-relay" },
                }),
                new FakeCallAdapter("telnyx-relay", carriesText: true)));

        Assert.Equal("/providers/speech", failure.Errors[0].Pointer);
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------------

    /// <summary>The one model every document here names, so the compile has something to resolve.</summary>
    private static IReadOnlyList<LlmProviderConfiguration> OneModel { get; } =
        [new LlmProviderConfiguration { Kind = "openai", Model = "gpt-4.1-mini", As = "reply" }];

    /// <summary>Writes one document that names both required blocks.</summary>
    /// <param name="callKind">The value <c>providers.call.kind</c> carries.</param>
    /// <param name="speechKind">The value both speech roles carry, <c>stt</c> and <c>tts</c> alike.</param>
    /// <returns>The document text.</returns>
    private static string Document(string callKind, string speechKind)
        => $$"""
           apiVersion: agentcore/v1
           name: call-registration
           agents:
             items:
               - { id: only, instructions: "I answer everything" }
           providers:
             call:   { kind: {{callKind}} }
             speech:
               stt: { kind: {{speechKind}} }
               tts: { kind: {{speechKind}} }
             llm:
               - { kind: openai, model: gpt-4.1-mini, as: reply }
           """;

    /// <summary>Builds a configuration the way a host that loads no document does.</summary>
    /// <param name="providers">The <c>providers</c> block, with one of the two required entries left out.</param>
    /// <returns>The configuration.</returns>
    private static AgentCoreConfiguration InCode(ProvidersConfiguration providers)
        => new()
        {
            ApiVersion = AgentCoreConfiguration.SupportedApiVersion,
            Name = "built-in-code",
            Agents = new AgentsConfiguration
            {
                Items = [new AgentConfiguration { Id = "only", Instructions = "I answer everything" }],
            },
            Providers = providers,
        };

    /// <summary>Starts a host on a document that names both kinds.</summary>
    /// <param name="callKind">The value <c>providers.call.kind</c> carries.</param>
    /// <param name="speechKind">The value both speech roles carry, <c>stt</c> and <c>tts</c> alike.</param>
    /// <param name="adapters">The call transports this host registers, if any.</param>
    /// <returns>The composed container.</returns>
    private static Task<ServiceProvider> BuildAsync(
        string callKind,
        string speechKind,
        params ICallAdapter[] adapters)
        => BuildFromAsync(ConfigurationLoader.LoadYaml(Document(callKind, speechKind)), adapters);

    /// <summary>Starts a host on one configuration, however that configuration was made.</summary>
    /// <param name="configuration">The document, loaded or built in code.</param>
    /// <param name="adapters">The call transports this host registers, if any.</param>
    /// <returns>The composed container.</returns>
    /// <remarks>
    /// <c>UseCall</c> is called only when this host has something to register. Calling it with an
    /// empty list would turn the seam on for a host that registered no vendor, which is the one
    /// thing these tests prove does not happen.
    /// </remarks>
    private static async Task<ServiceProvider> BuildFromAsync(
        AgentCoreConfiguration configuration,
        params ICallAdapter[] adapters)
    {
        ServiceCollection services = new();

        await services.AddAgentCoreAsync(
            options =>
            {
                options.Configuration = configuration;
                options.UseChatClients(_ => new RoutingChatClientFactory(new SequencedChatClient("hello")));
                options.UseSpeech(new TelnyxRelaySpeechAdapter());

                if (adapters.Length > 0)
                {
                    options.UseCall(adapters);
                }
            },
            TestContext.Current.CancellationToken);

        return services.BuildServiceProvider();
    }
}
