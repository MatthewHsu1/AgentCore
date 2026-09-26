using AgentCore.Application.Transcript;
using AgentCore.AspNetCore.Tests.Fakes;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Endpoints
{
    /// <summary>
    /// A text reply of two steps whose second step's tool fails until the run ends: the user saw step one's words
    /// and then the fallback line, so the history keeps both, in that order (owner ruling 2026-09-23).
    /// </summary>
    public sealed class ResponsesMultiStepFaultTests
    {
        private const string Fallback = "Sorry, the order service did not answer.";

        [Fact(Timeout = 30_000)]
        public async Task ToolFaultInStepTwo_KeepsStepOnesWordsThenTheFallback()
        {
            TwoStepChatClient model = new(["Let me check.", TwoStepChatClient.Call], [TwoStepChatClient.Call]);
            await using ResponsesHost host = await ResponsesHost.StartAsync(
                TwoStepChatClient.YamlWithFallback(Fallback), model, TwoStepChatClient.BindToolThatFailsAfterItsFirstCall);

            using HttpResponseMessage response = await host.PostAsync(
                /*lang=json,strict*/ """{ "stream": true, "conversation": "fault-step-two", "input": "first question" }""");
            List<string> shown = ResponsesHost.TextDeltas(await ResponsesHost.ReadEventsAsync(response));

            Assert.Equal("Let me check." + Fallback, string.Concat(shown));

            List<ConversationMessage> rows = await StoredRows.SettleAsync(host.Services, "fault-step-two", 4);
            string all = StoredRows.Describe(rows);
            Assert.True(rows[0].Content is { Text: "first question" } && rows[0].Content.Role == ChatRole.User, all);
            Assert.True(rows[1].Content is { Text: "Let me check." } && rows[1].Content.Contents.OfType<FunctionCallContent>().Any(), all);
            Assert.True(rows[2].Content.Contents.OfType<FunctionResultContent>().Any(), all);
            Assert.True(rows[^1].Content is { Text: Fallback } && rows[^1].Content.Role == ChatRole.Assistant, all);
            Assert.True(rows.Count(row => row.Content.Text.Length > 0) == 3, all);
        }
    }
}
