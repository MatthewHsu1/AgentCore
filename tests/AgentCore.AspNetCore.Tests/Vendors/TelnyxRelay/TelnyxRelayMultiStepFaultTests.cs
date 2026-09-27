using AgentCore.Application.Transcript;
using AgentCore.AspNetCore.Tests.Fakes;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Vendors.TelnyxRelay
{
    /// <summary>
    /// A voice reply of two steps whose second step's tool fails until the run ends: the caller hears step one's
    /// words and then the fallback line, and the history keeps both, in that order (owner rulings 2026-09-23).
    /// </summary>
    public sealed class TelnyxRelayMultiStepFaultTests
    {
        private const string Fallback = "Sorry, the order service did not answer.";

        [Fact(Timeout = 30_000)]
        public async Task ToolFaultInStepTwo_SpeaksStepOneThenTheFallback_AndTheHistoryKeepsBoth()
        {
            TwoStepChatClient model = new(["Let me check.", TwoStepChatClient.Call], [TwoStepChatClient.Call]);
            await using TelnyxRelayHost host = await TelnyxRelayHost.StartAsync(
                TwoStepChatClient.YamlWithFallback(Fallback), model, TwoStepChatClient.BindToolThatFailsAfterItsFirstCall);
            await using FakeRelayClient relay = await host.ConnectAsync();

            await relay.SendAsync(RelayFrames.Setup(conversationSessionId: "voice-fault-step-two"));
            await relay.SendAsync(RelayFrames.Prompt("hi", last: true));
            Assert.Equal("Let me check.", string.Concat(await relay.ReadTextFramesUntilLastAsync()));
            Assert.Equal(Fallback, string.Concat(await relay.ReadTextFramesUntilLastAsync()));

            List<ConversationMessage> rows = await StoredRows.SettleAsync(host.Services, "voice-fault-step-two", 4);
            string all = StoredRows.Describe(rows);
            Assert.True(rows[0].Content is { Text: "hi" } && rows[0].Content.Role == ChatRole.User, all);
            Assert.True(rows[1].Content is { Text: "Let me check." } && rows[1].Content.Contents.OfType<FunctionCallContent>().Any(), all);
            Assert.True(rows[2].Content.Contents.OfType<FunctionResultContent>().Any(), all);
            Assert.True(rows[^1].Content is { Text: Fallback } && rows[^1].Content.Role == ChatRole.Assistant, all);
            Assert.True(rows.Count(row => row.Content.Text.Length > 0) == 3, all);
        }
    }
}
