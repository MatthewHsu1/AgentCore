using System.Text;
using System.Text.Json.Nodes;
using AgentCore.Application.Transcript;
using AgentCore.AspNetCore.Tests.Fakes;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Vendors.TelnyxRelay
{
    /// <summary>
    /// A barge-in on a reply of two steps: step one speaks and calls a tool, step two speaks after the result.
    /// The history keeps the user's message, step one's words before its tool call, the finished pair, and the
    /// heard part of the step the caller cut (owner ruling 2026-09-23; LiveKit adds each step's
    /// <c>forwarded_text</c> as its own message, <c>agent_activity.py:3843-3861</c>).
    /// </summary>
    public sealed class TelnyxRelayMultiStepCutTests
    {
        private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

        // Probe V5_BargeInInSecondStep_KeepsBothStepsHeardText.
        [Fact(Timeout = 30_000)]
        public async Task BargeInDuringStepTwo_KeepsStepOneInPlaceAndTheHeardPartOfStepTwo()
        {
            TwoStepChatClient model = new(
                ["Let me check.", TwoStepChatClient.Call],
                ["Your order", TwoStepChatClient.Pause, " ships today"]);
            await using TelnyxRelayHost host = await TelnyxRelayHost.StartAsync(TwoStepChatClient.Yaml, model, TwoStepChatClient.BindTool);
            await using FakeRelayClient relay = await host.ConnectAsync();

            await relay.SendAsync(RelayFrames.Setup(conversationSessionId: "step-two-barge"));
            await relay.SendAsync(RelayFrames.Prompt("hi", last: true));
            await ReadTokensUntilAsync(relay, "Let me check.Your order");
            await model.Paused.Task.WaitAsync(Bound, TestContext.Current.CancellationToken);

            // Telnyx reports the utterance of the step it was playing.
            await relay.SendAsync(RelayFrames.Interrupt("Your order", durationMs: 500));

            List<ConversationMessage> rows = await StoredRows.SettleAsync(host.Services, "step-two-barge", 4);
            StoredRows.AssertTurn(rows, ("user", "hi", false, false), ("assistant", "Let me check.", true, false), ("tool", "", false, true), ("assistant", "Your order", false, false));
        }

        [Fact(Timeout = 30_000)]
        public async Task BargeInOnStepOneAfterItsToolRan_CutsStepOneToTheHeardWords()
        {
            TwoStepChatClient model = new(
                ["Let me check.", TwoStepChatClient.Call],
                [TwoStepChatClient.Pause, "Your order"]);
            await using TelnyxRelayHost host = await TelnyxRelayHost.StartAsync(TwoStepChatClient.Yaml, model, TwoStepChatClient.BindTool);
            await using FakeRelayClient relay = await host.ConnectAsync();

            await relay.SendAsync(RelayFrames.Setup(conversationSessionId: "step-one-barge"));
            await relay.SendAsync(RelayFrames.Prompt("hi", last: true));
            await ReadTokensUntilAsync(relay, "Let me check.");
            await model.Paused.Task.WaitAsync(Bound, TestContext.Current.CancellationToken);

            // Step two has said nothing, so the report names step one's utterance, still playing.
            await relay.SendAsync(RelayFrames.Interrupt("Let me", durationMs: 300));

            List<ConversationMessage> rows = await StoredRows.SettleAsync(host.Services, "step-one-barge", 3);
            StoredRows.AssertTurn(rows, ("user", "hi", false, false), ("assistant", "Let me", true, false), ("tool", "", false, true));
        }

        // Probe V7_CompletedMultiStepReply_KeepsBothSteps.
        [Fact(Timeout = 30_000)]
        public async Task CompletedTwoStepReply_KeepsBothStepsInOrder()
        {
            TwoStepChatClient model = new(
                ["Let me check.", TwoStepChatClient.Call],
                ["Your order", " ships today"]);
            await using TelnyxRelayHost host = await TelnyxRelayHost.StartAsync(TwoStepChatClient.Yaml, model, TwoStepChatClient.BindTool);
            await using FakeRelayClient relay = await host.ConnectAsync();

            await relay.SendAsync(RelayFrames.Setup(conversationSessionId: "two-steps"));
            await relay.SendAsync(RelayFrames.Prompt("hi", last: true));
            await ReadTokensUntilAsync(relay, "Let me check.Your order ships today");

            List<ConversationMessage> rows = await StoredRows.SettleAsync(host.Services, "two-steps", 4);
            StoredRows.AssertTurn(rows, ("user", "hi", false, false), ("assistant", "Let me check.", true, false), ("tool", "", false, true), ("assistant", "Your order ships today", false, false));
        }

        private static async Task ReadTokensUntilAsync(FakeRelayClient relay, string expected)
        {
            StringBuilder text = new();
            using CancellationTokenSource deadline = new(Bound);
            while (text.ToString() != expected)
            {
                JsonNode frame = await relay.ReadFrameAsync().WaitAsync(deadline.Token);
                if (frame["type"]?.GetValue<string>() == "text")
                {
                    _ = text.Append(frame["token"]!.GetValue<string>());
                }

                Assert.StartsWith(text.ToString(), expected, StringComparison.Ordinal);
            }
        }
    }
}
