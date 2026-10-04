using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.AspNetCore.DependencyInjection;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.AspNetCore.Vendors.TelnyxRelay;
using AgentCore.AspNetCore.Voice.Ports;
using AgentCore.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Conversation
{
    /// <summary>
    /// <c>providers.conversation</c> is selected while the host starts, and the pairing rule runs there too.
    /// </summary>
    public sealed class ConversationRegistrationTests
    {
        /// <summary>A conversation transport that is a name and a wire fact, which is all the port asks for.</summary>
        private sealed class FakeConversationAdapter(string kind, bool carriesText, CallTraits traits = CallTraits.None) : IConversationAdapter
        {
            public string Kind { get; } = kind;

            public bool CarriesText { get; } = carriesText;

            public CallTraits Traits { get; } = traits;
        }

        [Fact]
        public async Task AMatchingPairStarts()
        {
            using IHost provider = await BuildAsync(
                conversationKind: "telnyx-relay",
                speechKind: "telnyx-relay",
                new FakeConversationAdapter("telnyx-relay", carriesText: true));

            Assert.NotNull(provider.Services.GetService<IReadOnlyList<IConversationAdapter>>());
        }

        [Fact]
        public async Task AMismatchedPairFailsTheStartEvenWithNoRouteMapped()
        {
            ConfigurationLoadException failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
                async () => await BuildAsync(
                    conversationKind: "telnyx-relay",
                    speechKind: "deepgram",
                    new FakeConversationAdapter("telnyx-relay", carriesText: true)));

            Assert.Equal("/providers/speech/stt/kind", failure.Errors[0].Pointer);
        }

        [Fact]
        public async Task AHostThatRegistersNoConversationAdapterIsNotAskedAnything()
        {
            // The seam is off, exactly as telemetry, knowledge, and moderation are when nothing is
            // registered for them. A contradictory document is not read, and the start succeeds.
            using IHost provider = await BuildAsync(conversationKind: "telnyx-relay", speechKind: "deepgram");

            Assert.Null(provider.Services.GetService<IReadOnlyList<IConversationAdapter>>());
        }

        [Fact]
        public async Task ADocumentNamingAnUnregisteredConversationKindFailsTheStart()
        {
            ConfigurationLoadException failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
                async () => await BuildAsync(
                    conversationKind: "sip",
                    speechKind: "sip",
                    new FakeConversationAdapter("telnyx-relay", carriesText: true)));

            Assert.Equal("/providers/conversation/kind", failure.Errors[0].Pointer);
        }

        // The schema requires the conversation block, so a loaded document that writes a providers
        // section carries it. A configuration a host built in code passes through no schema at all,
        // and so does a loaded document that writes no providers section: the root requires only
        // apiVersion and name. Both routes reach the guard with a block missing, and both are refused
        // by a message that names the block rather than by a NullReferenceException.
        [Fact]
        public async Task AConfigurationBuiltInCodeWithNoConversationBlockFailsTheStart()
        {
            ConfigurationLoadException failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
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
                    new FakeConversationAdapter("telnyx-relay", carriesText: true)));

            Assert.Equal("/providers/conversation", failure.Errors[0].Pointer);
        }

        [Fact]
        public async Task AConfigurationBuiltInCodeWithNoSpeechBlockFailsTheStart()
        {
            ConfigurationLoadException failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
                async () => await BuildFromAsync(
                    InCode(new ProvidersConfiguration
                    {
                        Llm = OneModel,
                        Conversation = new ConversationProviderConfiguration { Kind = "telnyx-relay" },
                    }),
                    new FakeConversationAdapter("telnyx-relay", carriesText: true)));

            Assert.Equal("/providers/speech", failure.Errors[0].Pointer);
        }

        // A vendor that speaks for itself needs no providers.speech.
        [Fact]
        public async Task AnAdapterThatSpeaksForItselfStartsWithNoSpeechBlock()
        {
            using IHost host = await BuildFromAsync(
                InCode(new ProvidersConfiguration
                {
                    Llm = OneModel,
                    Conversation = new ConversationProviderConfiguration { Kind = "openai-live" },
                }),
                new FakeConversationAdapter("openai-live", carriesText: true, CallTraits.SpeaksForItself));

            Assert.NotNull(host.Services.GetService<IReadOnlyList<IConversationAdapter>>());
        }

        // A vendor that takes turns itself never runs AgentCore's fillers.
        [Fact]
        public async Task AVendorThatTakesTurnsItselfRefusesAToolFiller()
        {
            ConfigurationLoadException failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
                async () => await BuildFromAsync(
                    InCode(new ProvidersConfiguration
                    {
                        Llm = OneModel,
                        Conversation = new ConversationProviderConfiguration
                        {
                            Kind = "openai-live",
                            Filler = new Dictionary<string, VoiceFillerConfiguration>(StringComparer.Ordinal)
                            {
                                ["price_lookup"] = new() { Say = "One moment.", DelaySeconds = 1 },
                            },
                        },
                    }),
                    new FakeConversationAdapter("openai-live", carriesText: true, CallTraits.SpeaksForItself | CallTraits.TakesTurnsItself)));

            Assert.Equal("/providers/conversation/filler", failure.Errors[0].Pointer);
        }


        /// <summary>The one model every document here names, so the compile has something to resolve.</summary>
        private static IReadOnlyList<LlmProviderConfiguration> OneModel { get; } =
            [new LlmProviderConfiguration { Kind = "openai", Model = "gpt-4.1-mini", As = "reply" }];

        /// <summary>Writes one document that names both required blocks.</summary>
        /// <param name="conversationKind">The value <c>providers.conversation.kind</c> carries.</param>
        /// <param name="speechKind">The value both speech roles carry, <c>stt</c> and <c>tts</c> alike.</param>
        /// <returns>The document text.</returns>
        private static string Document(string conversationKind, string speechKind)
        {
            return $$"""
           apiVersion: agentcore/v1
           providers:
             conversation:   { kind: {{conversationKind}} }
             speech:
               stt: { kind: {{speechKind}} }
               tts: { kind: {{speechKind}} }
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

        /// <summary>Builds a configuration the way a host that loads no document does.</summary>
        /// <param name="providers">The <c>providers</c> block, with one of the two required entries left out.</param>
        /// <returns>The configuration.</returns>
        private static AgentCoreConfiguration InCode(ProvidersConfiguration providers)
        {
            return new()
            {
                ApiVersion = AgentCoreConfiguration.SupportedApiVersion,
                Agents = new AgentsConfiguration
                {
                    Items = [new AgentConfiguration { Id = "only", Instructions = "I answer everything" }],
                },
                Entries = new Dictionary<string, EntryConfiguration>
                {
                    ["main"] = new EntryConfiguration { Agent = "only" },
                },
                Providers = providers,
            };
        }

        /// <summary>Starts a host on a document that names both kinds.</summary>
        /// <param name="conversationKind">The value <c>providers.conversation.kind</c> carries.</param>
        /// <param name="speechKind">The value both speech roles carry, <c>stt</c> and <c>tts</c> alike.</param>
        /// <param name="adapters">The conversation transports this host registers, if any.</param>
        /// <returns>The composed container.</returns>
        private static Task<IHost> BuildAsync(
            string conversationKind,
            string speechKind,
            params IConversationAdapter[] adapters)
        {
            return BuildFromAsync(ConfigurationLoader.LoadYaml(Document(conversationKind, speechKind)), adapters);
        }

        /// <summary>Starts a host on one configuration, however that configuration was made.</summary>
        /// <param name="configuration">The document, loaded or built in code.</param>
        /// <param name="adapters">The conversation transports this host registers, if any.</param>
        /// <returns>The composed container.</returns>
        private static async Task<IHost> BuildFromAsync(
            AgentCoreConfiguration configuration,
            params IConversationAdapter[] adapters)
        {
            HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new());

            _ = builder.Services.AddAgentCore(options =>
            {
                options.Configuration = configuration;
                _ = options.UseChatClients(_ => new RoutingChatClientFactory(new FragmentingChatClient("hello")));
                _ = options.UseSpeech(new TelnyxRelaySpeechAdapter());

                if (adapters.Length > 0)
                {
                    _ = options.UseConversation(adapters);
                }
            });

            IHost host = builder.Build();
            try
            {
                await host.StartAsync(TestContext.Current.CancellationToken);
            }
            catch
            {
                // A failed start never stops what started, so disposal is the only cleanup path.
                host.Dispose();
                throw;
            }

            return host;
        }
    }
}
