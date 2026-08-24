using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.AspNetCore.Call;
using AgentCore.AspNetCore.DependencyInjection;
using AgentCore.AspNetCore.DependencyInjection.Startup;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Call;

/// <summary>
/// <c>providers.call</c> decides which transport answers the one call route, and whether a call
/// routes here at all.
/// </summary>
/// <remarks>
/// <para>
/// <b>No fake here names Telnyx, and this file references no vendor type.</b> That is the point of
/// the seam — spec §12. If this file ever needs a vendor type to prove the route, the selection has
/// leaked back into the vendor.
/// </para>
/// <para>
/// The selection happens while the host starts and not while the route is mapped, so these read
/// <see cref="CallSeamStartup"/> rather than <c>MapCall</c>. A host maps its routes on a built
/// application, which is after the document has been read; deciding there would mean a document
/// edit could silently leave the path a 404.
/// </para>
/// <para>
/// Every test runs offline. There is no account, no network call, and no API key anywhere in this
/// file.
/// </para>
/// </remarks>
public sealed class CallRouteSelectionTests
{
    [Fact]
    public void TheTransportTheDocumentNamesIsAskedForItsHandler()
    {
        var transport = new FakeTransport("bundled-fake");

        var seams = Build(callKind: "bundled-fake", transport);

        Assert.NotNull(seams.Handler);

        // The block handed over is the providers.call entry of this document, not null and not some
        // empty stand-in. The kind is what proves which entry it is.
        Assert.NotNull(transport.Configuration);
        Assert.Equal("bundled-fake", transport.Configuration.Kind);

        // And nothing reports the route as unroutable when one does route.
        Assert.Null(seams.Unroutable);
    }

    [Fact]
    public void AKindNoRegisteredAdapterServesFailsTheStartWithAPointer()
    {
        // The host registers a transport, and the document names a different vendor. That is a
        // deployment that would otherwise start with no inbound call route and no reason given, so
        // the boot must refuse it, with the pointer of the field the reader has to fix.
        var failure = Assert.Throws<ConfigurationLoadException>(
            () => Build(callKind: "no-such-vendor", new FakeTransport("bundled-fake")));

        Assert.Equal("/providers/call/kind", failure.Errors[0].Pointer);
    }

    [Fact]
    public void ADialOutVendorRoutesNothingAndSaysWhy()
    {
        var seams = Build(callKind: "dial-out-fake", new FakeDialOut("dial-out-fake"));

        // Section 12 asks this case to route nothing AND say so. A route that vanishes in silence is
        // how a deployment loses every call to a 404 with nothing to read.
        Assert.Null(seams.Handler);
        Assert.NotNull(seams.Unroutable);
        Assert.Contains("dial-out-fake", seams.Unroutable, StringComparison.Ordinal);
    }

    [Fact]
    public void AHostThatRegisteredNoTransportRoutesNothingAndSaysWhy()
    {
        var seams = CallSeamStartup.Build(
            ConfigurationLoader.LoadYaml(Document("bundled-fake")), new AgentCoreOptions());

        Assert.Null(seams.Handler);
        Assert.NotNull(seams.Unroutable);
        Assert.Contains("no call adapter", seams.Unroutable, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHostWithNoAgentCoreRegistrationAnswersTheRouteWithAReason()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        await using var app = builder.Build();
        app.MapCall("/v1/call");
        await app.StartAsync(TestContext.Current.CancellationToken);

        using HttpClient client = new() { BaseAddress = new Uri(Address(app)) };
        var response = await client.GetAsync("/v1/call", TestContext.Current.CancellationToken);

        // A readable refusal, and not the 404 a route that mapped nothing would have produced.
        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains(
            "registered no AgentCore services",
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheDefaultPatternIsVendorNeutral()
    {
        Assert.Equal("/v1/call", CallEndpointRouteBuilderExtensions.DefaultPattern);
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Runs the call seam over one document and the adapters a host registered.</summary>
    /// <param name="callKind">The value <c>providers.call.kind</c> carries.</param>
    /// <param name="adapters">The call vendors this host registers.</param>
    /// <returns>What the seam produced.</returns>
    private static CallSeamAdapters Build(string callKind, params ICallAdapter[] adapters)
    {
        AgentCoreOptions options = new();
        options.UseCall(adapters);
        options.UseSpeech(new FakeSpeech(callKind));

        return CallSeamStartup.Build(ConfigurationLoader.LoadYaml(Document(callKind)), options);
    }

    /// <summary>Reads back the port the server bound, since the test asked for any free one.</summary>
    /// <param name="app">The started application.</param>
    /// <returns>The base address to send to.</returns>
    private static string Address(WebApplication app)
        => app.Services
            .GetRequiredService<IServer>()
            .Features
            .Get<IServerAddressesFeature>()!
            .Addresses
            .First();

    /// <summary>Writes one document that names both blocks section 8.2 requires of one another.</summary>
    /// <param name="callKind">The value <c>providers.call.kind</c> carries.</param>
    /// <returns>The document text.</returns>
    /// <remarks>
    /// Written line by line here rather than at each call site, because the block sits under
    /// <c>providers:</c> and YAML indentation written by hand at a call site is how test documents
    /// go wrong. <c>speech</c> names the same kind as <c>call</c> because the schema requires the
    /// block to exist; nothing in this file selects a speech vendor.
    /// </remarks>
    private static string Document(string callKind)
        => $$"""
           apiVersion: agentcore/v1
           name: call-route-selection
           agents:
             items:
               - { id: only, instructions: "I answer everything" }
           providers:
             call:   { kind: {{callKind}} }
             speech:
               stt: { kind: {{callKind}} }
               tts: { kind: {{callKind}} }
             llm:
               - { kind: openai, model: gpt-4.1-mini, as: reply }
           """;

    /// <summary>A transport that answers a call and names no vendor.</summary>
    /// <remarks>
    /// It records the block the seam is supposed to hand it. Recording only that
    /// <c>CreateHandler</c> ran would let a seam that resolved the document block to something other
    /// than the one the reader wrote still pass.
    /// </remarks>
    private sealed class FakeTransport(string kind) : ICallTransportAdapter
    {
        public string Kind { get; } = kind;

        public bool CarriesText => true;

        public CallProviderConfiguration? Configuration { get; private set; }

        public RequestDelegate CreateHandler(CallProviderConfiguration configuration)
        {
            Configuration = configuration;
            return _ => Task.CompletedTask;
        }
    }

    /// <summary>A vendor this process dials out to. It has no route to answer.</summary>
    private sealed class FakeDialOut(string kind) : ICallAdapter
    {
        public string Kind { get; } = kind;

        public bool CarriesText => false;
    }

    /// <summary>The speech vendor the pairing rule reads, which builds nothing.</summary>
    private sealed class FakeSpeech(string kind) : ISpeechAdapter
    {
        public string Kind { get; } = kind;
    }
}
