using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.AspNetCore.Call;
using AgentCore.AspNetCore.Tests.Fakes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Call;

/// <summary>
/// <c>providers.call</c> decides which transport answers the one call route, and whether one is
/// mapped at all.
/// </summary>
/// <remarks>
/// <para>
/// <b>No fake here names Telnyx, and this file references no vendor type.</b> That is the point of
/// the seam — spec §12. If this file ever needs a vendor type to prove the route, the selection has
/// leaked back into the vendor.
/// </para>
/// <para>
/// Every test runs offline. There is no account, no network call, and no API key anywhere in this
/// file.
/// </para>
/// </remarks>
public sealed class CallRouteSelectionTests
{
    /// <summary>A transport that maps a route and names no vendor.</summary>
    /// <remarks>
    /// It records both arguments the seam is supposed to hand it. Recording only that
    /// <c>Map</c> ran would let a <c>MapCall</c> that passed the wrong route, or that resolved the
    /// document block to something other than the one the reader wrote, still pass.
    /// </remarks>
    private sealed class FakeTransport(string kind) : ICallTransportAdapter
    {
        public string Kind { get; } = kind;

        public bool CarriesText => true;

        public bool Mapped { get; private set; }

        public string? Pattern { get; private set; }

        public CallProviderConfiguration? Configuration { get; private set; }

        public IEndpointConventionBuilder Map(
            IEndpointRouteBuilder endpoints,
            string pattern,
            CallProviderConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(endpoints);

            Mapped = true;
            Pattern = pattern;
            Configuration = configuration;
            return endpoints.Map(pattern, () => Results.Ok());
        }
    }

    /// <summary>A vendor this process dials out to. It has no route to map.</summary>
    private sealed class FakeDialOut(string kind) : ICallAdapter
    {
        public string Kind { get; } = kind;

        public bool CarriesText => false;
    }

    [Fact]
    public void TheTransportTheDocumentNamesIsMapped()
    {
        var transport = new FakeTransport("bundled-fake");

        var app = BuildApp(callKind: "bundled-fake", transport);
        app.MapCall("/v1/call");

        Assert.True(transport.Mapped);

        // The route the host asked for reaches the transport unchanged: the seam picks the vendor
        // and never rewrites the path.
        Assert.Equal("/v1/call", transport.Pattern);

        // And the block handed over is the providers.call entry of this document, not null and not
        // some empty stand-in. The kind is what proves which entry it is.
        Assert.NotNull(transport.Configuration);
        Assert.Equal("bundled-fake", transport.Configuration.Kind);
    }

    [Fact]
    public void AKindNoRegisteredAdapterServesFailsTheStartWithAPointer()
    {
        // The host registers a transport, and the document names a different vendor. That is a
        // deployment that would otherwise start with no inbound call route and no reason given, so
        // MapCall must refuse it while the host is still starting, with the pointer of the field
        // the reader has to fix.
        var app = BuildApp(callKind: "no-such-vendor", new FakeTransport("bundled-fake"));

        var failure = Assert.Throws<ConfigurationLoadException>(() => app.MapCall("/v1/call"));

        Assert.Equal("/providers/call/kind", failure.Errors[0].Pointer);
    }

    [Fact]
    public void ADialOutVendorMapsNothing()
    {
        EventObservedLoggerProvider capture = new("DialOutVendorMapsNoRoute");
        var app = BuildApp(callKind: "dial-out-fake", capture, new FakeDialOut("dial-out-fake"));

        var builder = app.MapCall("/v1/call");

        // Nothing was mapped, and the builder handed back is the shared no-op that says so. The
        // identity is what the assertion pins: adding a convention to it proves nothing, because
        // IEndpointConventionBuilder.Add only stores the delegate and this test never materialises
        // an endpoint data source to run it against.
        Assert.Same(UnmappedEndpoint.Instance, builder);

        // Section 12 asks this case to map nothing AND say so. A route that vanishes in silence is
        // how a deployment loses every call to a 404 with nothing to read.
        Assert.Equal(1, capture.Count);
    }

    [Fact]
    public void AHostWithNoRegistrationMapsNothing()
    {
        EventObservedLoggerProvider capture = new("RouteNotMapped");
        var app = BuildAppWithNoAgentCore(capture);

        var builder = app.MapCall("/v1/call");

        Assert.Same(UnmappedEndpoint.Instance, builder);

        // The other half of the same rule: nothing mapped, and one line naming the reason.
        Assert.Equal(1, capture.Count);
    }

    [Fact]
    public void TheDefaultPatternIsVendorNeutral()
    {
        Assert.Equal("/v1/call", CallEndpointRouteBuilderExtensions.DefaultPattern);
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Builds a host whose document names one call kind and which registers some adapters.</summary>
    /// <param name="callKind">The value <c>providers.call.kind</c> carries.</param>
    /// <param name="adapters">The call vendors this host registers.</param>
    /// <returns>The built application, with nothing mapped yet.</returns>
    /// <remarks>
    /// The document and the adapter list are registered straight into the container rather than
    /// through <c>AddAgentCoreAsync</c>, because those two registrations are all
    /// <see cref="CallEndpointRouteBuilderExtensions.MapCall(IEndpointRouteBuilder, string)"/> reads,
    /// and a full composition would drag a model and the pairing rule into a test about routing.
    /// </remarks>
    private static WebApplication BuildApp(string callKind, params ICallAdapter[] adapters)
        => BuildApp(callKind, observer: null, adapters);

    /// <summary>Builds the same host, with one provider watching for a named log line.</summary>
    /// <param name="callKind">The value <c>providers.call.kind</c> carries.</param>
    /// <param name="observer">The provider that counts one named line, or null for none.</param>
    /// <param name="adapters">The call vendors this host registers.</param>
    /// <returns>The built application, with nothing mapped yet.</returns>
    private static WebApplication BuildApp(
        string callKind,
        ILoggerProvider? observer,
        params ICallAdapter[] adapters)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        if (observer is not null)
        {
            builder.Logging.AddProvider(observer);
        }

        builder.Services.AddSingleton(ConfigurationLoader.LoadYaml(Document(callKind)));
        builder.Services.AddSingleton<IReadOnlyList<ICallAdapter>>(adapters);
        return builder.Build();
    }

    /// <summary>Builds a host that never called <c>AddAgentCoreAsync</c> at all.</summary>
    /// <param name="observer">The provider that counts one named line, or null for none.</param>
    /// <returns>The built application, with no AgentCore service in its container.</returns>
    private static WebApplication BuildAppWithNoAgentCore(ILoggerProvider? observer = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        if (observer is not null)
        {
            builder.Logging.AddProvider(observer);
        }

        return builder.Build();
    }

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
}
