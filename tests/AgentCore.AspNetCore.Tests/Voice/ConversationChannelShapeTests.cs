using AgentCore.AspNetCore.Voice;
using AgentCore.AspNetCore.Tests.Fakes;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Voice
{
    /// <summary>
    /// The voice loop serves a split channel exactly as it serves a bundled one.
    /// </summary>
    public sealed class ConversationChannelShapeTests
    {
        [Fact(Timeout = 30_000)]
        public async Task ASplitChannelOfTwoObjects_RunsAConversationThroughTheVoiceLoop()
        {
            using FragmentingChatClient reply = new("hello there");
            FakeConversationOutput output = new();
            FakeConversationInput input = new(
                new ConversationInput.Started("conversation-shape"),
                new ConversationInput.Utterance("hello", "en", IsFinal: true));
            ConversationChannel channel = new(input, output);
            VoiceLoopHarness harness = VoiceLoopHarness.Create(reply, channel.Output);

            await harness.Loop.RunAsync(channel.Input.ListenAsync(TestContext.Current.CancellationToken));
            await Poll.UntilAsync(() => output.Completions == 1);

            Assert.NotEmpty(output.Spoken);
            Assert.Equal(1, output.Completions);

            await harness.Loop.DrainAsync();
            await channel.DisposeAsync();
            Assert.Equal(1, input.Disposals);
            Assert.Equal(1, output.Disposals);
        }
    }
}
