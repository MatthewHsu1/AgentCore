using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using Xunit;

namespace AgentCore.Application.Tests.Configuration
{
    /// <summary>
    /// <c>providers.conversation</c> names the vendor that carries the conversation, and the limits of its socket.
    /// </summary>
    public sealed class ConversationSchemaTests
    {
        [Fact]
        public void TheConversationBlockBindsItsKindAndItsThreeKnobs()
        {
            AgentCoreConfiguration configuration = Load("""
            providers:
              conversation:
                kind: telnyx-relay
                idleTimeoutSeconds: 30
                closeTimeoutSeconds: 5
                maxFrameBytes: 65536
              speech:
                stt: { kind: telnyx-relay }
                tts: { kind: telnyx-relay }
            """);

            ConversationProviderConfiguration conversation = configuration.Providers!.Conversation!;
            Assert.Equal("telnyx-relay", conversation.Kind);
            Assert.Equal(30, conversation.IdleTimeoutSeconds);
            Assert.Equal(5, conversation.CloseTimeoutSeconds);
            Assert.Equal(65536, conversation.MaxFrameBytes);
        }

        [Fact]
        public void TheThreeKnobsAreOptional()
        {
            AgentCoreConfiguration configuration = Load("""
            providers:
              conversation:   { kind: telnyx-relay }
              speech:
                stt: { kind: telnyx-relay }
                tts: { kind: telnyx-relay }
            """);

            ConversationProviderConfiguration conversation = configuration.Providers!.Conversation!;
            Assert.Null(conversation.IdleTimeoutSeconds);
            Assert.Null(conversation.CloseTimeoutSeconds);
            Assert.Null(conversation.MaxFrameBytes);
        }

        [Fact]
        public void AMissingConversationBlockFailsTheLoad()
        {
            ConfigurationLoadException failure = Assert.Throws<ConfigurationLoadException>(
                () => Load("""
                providers:
                  speech:
                    stt: { kind: telnyx-relay }
                    tts: { kind: telnyx-relay }
                """));

            Assert.Equal(ConfigurationCheck.DocumentSchema, failure.Check);
            Assert.Contains("conversation", failure.Message, StringComparison.Ordinal);
        }

        // answerSeconds and the vendor-owned live: block.
        [Fact]
        public void TheAnswerDeadlineAndTheVendorBlockBind()
        {
            AgentCoreConfiguration configuration = Load("""
            providers:
              conversation:
                kind: openai-live
                answerSeconds: 3
                live:
                  instructions: "Be brief."
                  voice: marin
            """);

            ConversationProviderConfiguration conversation = configuration.Providers!.Conversation!;
            Assert.Equal(3, conversation.AnswerSeconds);
            Assert.Equal("Be brief.", conversation.Live.GetProperty("instructions").GetString());
            Assert.Equal("marin", conversation.Live.GetProperty("voice").GetString());
        }

        [Fact]
        public void AnAnswerDeadlineOfZeroFailsTheLoad()
        {
            ConfigurationLoadException failure = Assert.Throws<ConfigurationLoadException>(
                () => Load("""
                providers:
                  conversation: { kind: openai-live, answerSeconds: 0 }
                """));

            Assert.Equal(ConfigurationCheck.DocumentSchema, failure.Check);
            Assert.Contains("answerSeconds", failure.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void AnUnknownFieldUnderConversationFailsTheLoad()
        {
            ConfigurationLoadException failure = Assert.Throws<ConfigurationLoadException>(
                () => Load("""
                providers:
                  conversation:   { kind: telnyx-relay, idleTimeoutSecnods: 30 }
                  speech:
                    stt: { kind: telnyx-relay }
                    tts: { kind: telnyx-relay }
                """));

            // additionalProperties names the offending property in the pointer rather than the text, and
            // ConfigurationLoadException.Message carries pointer and message together for every error.
            Assert.Contains("idleTimeoutSecnods", failure.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void InfiniteIsSpelledMinusOne()
        {
            AgentCoreConfiguration configuration = Load("""
            providers:
              conversation:   { kind: telnyx-relay, idleTimeoutSeconds: -1 }
              speech:
                stt: { kind: telnyx-relay }
                tts: { kind: telnyx-relay }
            """);

            Assert.Equal(-1, configuration.Providers!.Conversation!.IdleTimeoutSeconds);
        }

        [Fact]
        public void TheSpeechBlockBindsBothRoles()
        {
            AgentCoreConfiguration configuration = Load("""
            providers:
              conversation: { kind: sip }
              speech:
                stt: { kind: deepgram }
                tts: { kind: elevenlabs }
            """);

            SpeechProviderConfiguration speech = configuration.Providers!.Speech!;
            Assert.Equal("deepgram", speech.Stt.Kind);
            Assert.Equal("elevenlabs", speech.Tts.Kind);
        }

        [Fact]
        public void ASpeechBlockWithoutRecognitionFailsTheLoad()
        {
            ConfigurationLoadException failure = Assert.Throws<ConfigurationLoadException>(
                () => Load("""
                providers:
                  conversation: { kind: telnyx-relay }
                  speech:
                    tts: { kind: telnyx-relay }
                """));

            Assert.Equal(ConfigurationCheck.DocumentSchema, failure.Check);
            Assert.Contains("stt", failure.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void ASpeechBlockWithoutSynthesisFailsTheLoad()
        {
            ConfigurationLoadException failure = Assert.Throws<ConfigurationLoadException>(
                () => Load("""
                providers:
                  conversation: { kind: telnyx-relay }
                  speech:
                    stt: { kind: telnyx-relay }
                """));

            Assert.Equal(ConfigurationCheck.DocumentSchema, failure.Check);
            Assert.Contains("tts", failure.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void AnUnknownFieldUnderSpeechFailsTheLoad()
        {
            ConfigurationLoadException failure = Assert.Throws<ConfigurationLoadException>(
                () => Load("""
                providers:
                  conversation: { kind: telnyx-relay }
                  speech:
                    stt:   { kind: telnyx-relay }
                    tts:   { kind: telnyx-relay }
                    voice: alloy
                """));

            // additionalProperties names the offending property in the pointer rather than the text, and
            // ConfigurationLoadException.Message carries pointer and message together for every error.
            // The field is one no speech document writes, so the assertion cannot pass on another failure.
            Assert.Equal(ConfigurationCheck.DocumentSchema, failure.Check);
            Assert.Contains("voice", failure.Message, StringComparison.Ordinal);
        }

        /// <summary>Loads one <c>providers:</c> section under the smallest complete document header.</summary>
        /// <param name="providers">The <c>providers:</c> section, written at the document's own margin.</param>
        /// <returns>The loaded document.</returns>
        private static AgentCoreConfiguration Load(string providers)
        {
            return ConfigurationLoader.LoadYaml(
                        "apiVersion: agentcore/v1\nagents:\n  items:\n    - { id: only, instructions: \"ok\" }\nentries:\n  main:\n    agent: only\n" + providers);
        }
    }
}
