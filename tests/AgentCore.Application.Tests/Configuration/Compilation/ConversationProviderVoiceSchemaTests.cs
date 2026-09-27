using System.Text.Json;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using Xunit;

namespace AgentCore.Application.Tests.Configuration.Compilation
{
    /// <summary>
    /// <c>providers.conversation.userAway</c> and <c>providers.conversation.filler</c>: the voice options
    /// surface of plan step 8. A document that omits either key opens no away prompt override and no
    /// filler; the shipped defaults are an <see cref="AgentCore.AspNetCore"/> concern, not this layer's.
    /// </summary>
    public sealed class ConversationProviderVoiceSchemaTests
    {
        private const string MinimalYaml =
            """
        apiVersion: agentcore/v1
        providers:
          conversation:   { kind: telnyx-relay }
          speech:
            stt: { kind: telnyx-relay }
            tts: { kind: telnyx-relay }
        agents:
          items:
            - { id: only, instructions: "hello" }
        entries:
          main:
            agent: only
        """;

        [Fact]
        public void AnOmittedUserAwayBindsAsUndefined()
        {
            ConversationProviderConfiguration conversation = Load(MinimalYaml).Providers!.Conversation!;

            Assert.Equal(JsonValueKind.Undefined, conversation.UserAway.ValueKind);
            Assert.Empty(conversation.Filler);
        }

        [Fact]
        public void AnExplicitNullUserAwayBindsAsNull()
        {
            const string yaml =
                """
            apiVersion: agentcore/v1
            providers:
              conversation:   { kind: telnyx-relay, userAway: null }
              speech:
                stt: { kind: telnyx-relay }
                tts: { kind: telnyx-relay }
            agents:
              items:
                - { id: only, instructions: "hello" }
            entries:
              main:
                agent: only
            """;

            ConversationProviderConfiguration conversation = Load(yaml).Providers!.Conversation!;

            Assert.Equal(JsonValueKind.Null, conversation.UserAway.ValueKind);
        }

        [Fact]
        public void AFullUserAwayAndFillerBlockBindsEveryField()
        {
            const string yaml =
                """
            apiVersion: agentcore/v1
            providers:
              conversation:
                kind: telnyx-relay
                userAway:
                  timeoutSeconds: 30
                  say: "Are you still there?"
                filler:
                  lookup_order:
                    say: "One moment while I look that up."
                    delaySeconds: 1.5
                    intervalSeconds: 5
                    maxSteps: 2
              speech:
                stt: { kind: telnyx-relay }
                tts: { kind: telnyx-relay }
            agents:
              items:
                - { id: only, instructions: "hello" }
            entries:
              main:
                agent: only
            """;

            ConversationProviderConfiguration conversation = Load(yaml).Providers!.Conversation!;

            Assert.Equal(JsonValueKind.Object, conversation.UserAway.ValueKind);
            Assert.Equal(30, conversation.UserAway.GetProperty("timeoutSeconds").GetDouble());
            Assert.Equal("Are you still there?", conversation.UserAway.GetProperty("say").GetString());

            VoiceFillerConfiguration filler = Assert.Single(conversation.Filler).Value;
            Assert.Equal("One moment while I look that up.", filler.Say);
            Assert.Equal(1.5, filler.DelaySeconds);
            Assert.Equal(5, filler.IntervalSeconds);
            Assert.Equal(2, filler.MaxSteps);
        }

        [Fact]
        public void AUserAwayMissingSayIsRejectedBySchemaValidation()
        {
            const string yaml =
                """
            apiVersion: agentcore/v1
            providers:
              conversation:   { kind: telnyx-relay, userAway: { timeoutSeconds: 15 } }
              speech:
                stt: { kind: telnyx-relay }
                tts: { kind: telnyx-relay }
            agents:
              items:
                - { id: only, instructions: "hello" }
            entries:
              main:
                agent: only
            """;

            ConfigurationLoadException failure = Assert.Throws<ConfigurationLoadException>(() => Load(yaml));

            Assert.Contains("say", failure.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void AFillerEntryMissingDelaySecondsIsRejectedBySchemaValidation()
        {
            const string yaml =
                """
            apiVersion: agentcore/v1
            providers:
              conversation:
                kind: telnyx-relay
                filler:
                  lookup_order:
                    say: "One moment"
              speech:
                stt: { kind: telnyx-relay }
                tts: { kind: telnyx-relay }
            agents:
              items:
                - { id: only, instructions: "hello" }
            entries:
              main:
                agent: only
            """;

            ConfigurationLoadException failure = Assert.Throws<ConfigurationLoadException>(() => Load(yaml));

            Assert.Contains("delaySeconds", failure.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void AnUnknownKeyInsideFillerIsRejectedBySchemaValidation()
        {
            const string yaml =
                """
            apiVersion: agentcore/v1
            providers:
              conversation:
                kind: telnyx-relay
                filler:
                  lookup_order:
                    say: "One moment"
                    delaySeconds: 1
                    priority: high
              speech:
                stt: { kind: telnyx-relay }
                tts: { kind: telnyx-relay }
            agents:
              items:
                - { id: only, instructions: "hello" }
            entries:
              main:
                agent: only
            """;

            ConfigurationLoadException failure = Assert.Throws<ConfigurationLoadException>(() => Load(yaml));

            Assert.Contains("priority", failure.Message, StringComparison.Ordinal);
        }

        private static AgentCoreConfiguration Load(string yaml)
        {
            return ConfigurationLoader.LoadYaml(yaml);
        }
    }
}
