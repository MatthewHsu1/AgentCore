using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.AspNetCore.DependencyInjection;
using AgentCore.AspNetCore.DependencyInjection.Startup;
using AgentCore.AspNetCore.Voice.Ports;
using AgentCore.AspNetCore.Voice.Routing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Voice
{
    /// <summary>
    /// <c>providers.conversation</c> decides which transport answers the one conversation route, and whether a conversation
    /// routes here at all.
    /// </summary>
    public sealed class ConversationRouteSelectionTests
    {
        [Fact]
        public void TheTransportTheDocumentNamesIsAskedForItsHandler()
        {
            FakeTransport transport = new("bundled-fake");

            ConversationSeamAdapters seams = Build(conversationKind: "bundled-fake", transport);

            Assert.NotNull(seams.Route);

            // The block handed over is the providers.conversation entry of this document, not null and not some
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
            // deployment that would otherwise start with no inbound conversation route and no reason given, so
            // the boot must refuse it, with the pointer of the field the reader has to fix.
            ConfigurationLoadException failure = Assert.Throws<ConfigurationLoadException>(
                () => Build(conversationKind: "no-such-vendor", new FakeTransport("bundled-fake")));

            Assert.Equal("/providers/conversation/kind", failure.Errors[0].Pointer);
        }

        [Fact]
        public void ADialOutVendorRoutesNothingAndSaysWhy()
        {
            ConversationSeamAdapters seams = Build(conversationKind: "dial-out-fake", new FakeDialOut("dial-out-fake"));

            // This case must route nothing AND say so. A route that vanishes in silence is
            // how a deployment loses every call to a 404 with nothing to read.
            Assert.Null(seams.Route);
            Assert.NotNull(seams.Unroutable);
            Assert.Contains("dial-out-fake", seams.Unroutable, StringComparison.Ordinal);
        }

        [Fact]
        public void AHostThatRegisteredNoTransportRoutesNothingAndSaysWhy()
        {
            ConversationSeamAdapters seams = ConversationSeamStartup.Build(
                ConfigurationLoader.LoadYaml(Document("bundled-fake")), new AgentCoreOptions());

            Assert.Null(seams.Route);
            Assert.NotNull(seams.Unroutable);
            Assert.Contains("no conversation adapter", seams.Unroutable, StringComparison.Ordinal);
        }

        [Fact]
        public void MappingTheCallRouteOnAHostWithNoAgentCoreRegistrationFailsAtStartup()
        {
            WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
            _ = builder.Logging.ClearProviders();
            WebApplication app = builder.Build();

            InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() => app.MapCall());

            Assert.Contains("AddAgentCore", failure.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void TheDefaultPatternIsVendorNeutral()
        {
            Assert.Equal("/v1/{entry}/call", ConversationEndpointRouteBuilderExtensions.DefaultPattern);
        }


        /// <summary>Runs the conversation seam over one document and the adapters a host registered.</summary>
        /// <param name="conversationKind">The value <c>providers.conversation.kind</c> carries.</param>
        /// <param name="adapters">The conversation vendors this host registers.</param>
        /// <returns>What the seam produced.</returns>
        private static ConversationSeamAdapters Build(string conversationKind, params IConversationAdapter[] adapters)
        {
            AgentCoreOptions options = new();
            _ = options.UseConversation(adapters);
            _ = options.UseSpeech(new FakeSpeech(conversationKind));

            return ConversationSeamStartup.Build(ConfigurationLoader.LoadYaml(Document(conversationKind)), options);
        }

        /// <summary>Reads back the port the server bound, since the test asked for any free one.</summary>
        /// <param name="app">The started application.</param>
        /// <returns>The base address to send to.</returns>
        private static string Address(WebApplication app)
        {
            return app.Services
                        .GetRequiredService<IServer>()
                        .Features
                        .Get<IServerAddressesFeature>()!
                        .Addresses
                        .First();
        }

        /// <summary>Writes one document that names both blocks that require each other.</summary>
        /// <param name="conversationKind">The value <c>providers.conversation.kind</c> carries.</param>
        /// <returns>The document text.</returns>
        private static string Document(string conversationKind)
        {
            return $$"""
           apiVersion: agentcore/v1
           providers:
             conversation:   { kind: {{conversationKind}} }
             speech:
               stt: { kind: {{conversationKind}} }
               tts: { kind: {{conversationKind}} }
             llm:
               - { kind: openai, model: gpt-4.1-mini, as: reply }
           agents:
             items:
               - { id: dummy, instructions: "I answer everything" }
           entries:
             main:
               agent: dummy
           """;
        }

        /// <summary>A transport that answers a conversation and names no vendor.</summary>
        private sealed class FakeTransport(string kind) : IConversationTransportAdapter
        {
            public string Kind { get; } = kind;

            public bool CarriesText => true;

            public ConversationProviderConfiguration? Configuration { get; private set; }

            public ConversationRoute CreateRoute(ConversationProviderConfiguration configuration)
            {
                Configuration = configuration;
                return new ConversationRoute(_ => Task.CompletedTask, _ => ValueTask.FromResult(true));
            }
        }

        /// <summary>A vendor this process dials out to. It has no route to answer.</summary>
        private sealed class FakeDialOut(string kind) : IConversationAdapter
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
}
