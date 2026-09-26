using System.Text;
using System.Text.Json.Nodes;
using AgentCore.Application.Transcript;
using AgentCore.AspNetCore.Tests.Fakes;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Endpoints
{
    /// <summary>
    /// A text stream aborted inside a reply of two steps keeps every word it yielded, each step's words in their
    /// own place around the finished tool pair (owner ruling 2026-09-23).
    /// </summary>
    public sealed class ResponsesMultiStepAbortTests
    {
        private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

        // Probe T2_TextStreamAbortInSecondStep_KeepsAllYieldedText.
        [Fact(Timeout = 30_000)]
        public async Task AbortDuringStepTwo_KeepsStepOneInPlaceAndStepTwosYieldedWords()
        {
            TwoStepChatClient model = new(
                ["Let me check.", TwoStepChatClient.Call],
                ["Your order", TwoStepChatClient.Pause, " ships today"]);
            await using ResponsesHost host = await ResponsesHost.StartAsync(TwoStepChatClient.Yaml, model, TwoStepChatClient.BindTool);

            HttpResponseMessage response = await host.PostAsync(
                /*lang=json,strict*/ """{ "stream": true, "conversation": "abort-step-two", "input": "first question" }""");
            await ReadDeltasUntilAsync(response, "Let me check.Your order");
            await model.Paused.Task.WaitAsync(Bound, TestContext.Current.CancellationToken);
            response.Dispose();

            List<ConversationMessage> rows = await StoredRows.SettleAsync(host.Services, "abort-step-two", 4);
            StoredRows.AssertTurn(rows, ("user", "first question", false, false), ("assistant", "Let me check.", true, false), ("tool", "", false, true), ("assistant", "Your order", false, false));
        }

        // Probe T3_TextStreamAbortAfterToolResultBeforeStepTwoText_KeepsStepOneText.
        [Fact(Timeout = 30_000)]
        public async Task AbortAfterTheToolResultBeforeStepTwoSpeaks_KeepsStepOneInPlace()
        {
            TwoStepChatClient model = new(
                ["Let me check.", TwoStepChatClient.Call],
                [TwoStepChatClient.Pause, "Your order"]);
            await using ResponsesHost host = await ResponsesHost.StartAsync(TwoStepChatClient.Yaml, model, TwoStepChatClient.BindTool);

            HttpResponseMessage response = await host.PostAsync(
                /*lang=json,strict*/ """{ "stream": true, "conversation": "abort-before-step-two", "input": "first question" }""");
            await ReadDeltasUntilAsync(response, "Let me check.");
            await model.Paused.Task.WaitAsync(Bound, TestContext.Current.CancellationToken);
            response.Dispose();

            List<ConversationMessage> rows = await StoredRows.SettleAsync(host.Services, "abort-before-step-two", 3);
            StoredRows.AssertTurn(rows, ("user", "first question", false, false), ("assistant", "Let me check.", true, false), ("tool", "", false, true));
        }

        private static async Task ReadDeltasUntilAsync(HttpResponseMessage response, string expected)
        {
            using StreamReader reader = new(await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken), Encoding.UTF8, leaveOpen: true);
            StringBuilder text = new();
            using CancellationTokenSource deadline = new(Bound);
            while (text.ToString() != expected)
            {
                string? line = await reader.ReadLineAsync(deadline.Token);
                Assert.NotNull(line);
                if (line.StartsWith("data: ", StringComparison.Ordinal)
                    && JsonNode.Parse(line["data: ".Length..]) is JsonObject frame
                    && frame["type"]?.GetValue<string>() == "response.output_text.delta")
                {
                    _ = text.Append(frame["delta"]!.GetValue<string>());
                }
            }
        }
    }
}
