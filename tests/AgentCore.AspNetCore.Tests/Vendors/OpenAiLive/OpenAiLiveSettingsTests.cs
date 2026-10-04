using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.AspNetCore.Vendors.OpenAiLive;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Vendors.OpenAiLive
{
    /// <summary>The live: block holds instructions, voice and model, and nothing else.</summary>
    public sealed class OpenAiLiveSettingsTests
    {
        [Fact]
        public void InstructionsAloneTakeTheDefaultVoiceAndModel()
        {
            Assert.Equal(new OpenAiLiveSettings("Be brief.", "marin", "gpt-live-1"), OpenAiLiveSettings.From(Conversation("live: { instructions: \"Be brief.\" }")));
        }

        [Fact]
        public void AVoiceAndAModelAreTaken()
        {
            Assert.Equal(
                new OpenAiLiveSettings("Be brief.", "cedar", "gpt-live-2"),
                OpenAiLiveSettings.From(Conversation("live: { instructions: \"Be brief.\", voice: cedar, model: gpt-live-2 }")));
        }

        [Theory]
        [InlineData("live: { voice: marin }", "/providers/conversation/live/instructions")]
        [InlineData("live: { instructions: \"x\", webhookSecret: \"${secret:OPENAI_WEBHOOK_SECRET}\" }", "/providers/conversation/live/webhookSecret")]
        [InlineData("answerSeconds: 5", "/providers/conversation/live")]
        public void AWrongBlockFailsWithAPointer(string block, string errorPath)
        {
            ConfigurationLoadException failure = Assert.Throws<ConfigurationLoadException>(() => OpenAiLiveSettings.From(Conversation(block)));

            Assert.Equal(errorPath, failure.Errors[0].Pointer);
        }

        private static ConversationProviderConfiguration Conversation(string block)
        {
            return ConfigurationLoader.LoadYaml(
                "apiVersion: agentcore/v1\nagents:\n  items:\n    - { id: only, instructions: \"ok\" }\nentries:\n  main:\n    agent: only\n"
                + "providers:\n  conversation:\n    kind: openai-live\n    " + block + "\n").Providers!.Conversation!;
        }
    }
}
