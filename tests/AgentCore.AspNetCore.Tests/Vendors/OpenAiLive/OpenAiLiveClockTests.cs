using System.Text.Json.Nodes;
using AgentCore.AspNetCore.Tests.Fakes;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Vendors.OpenAiLive
{
    /// <summary>
    /// On a real SIP call GPT-Live told the caller it was Wednesday; it was Sunday. OpenAI's delegation guide gives
    /// session-wide facts with <c>session.instructions.append</c> and <c>delegation_id: null</c>. The call opens with
    /// the clock line the back office reads each turn, in its format and zone, when the entry's agent keeps
    /// <c>clock:</c> on.
    /// </summary>
    public sealed class OpenAiLiveClockTests
    {
        private const string ClockOnYaml = """
        apiVersion: agentcore/v1
        agents:
          items:
            - { id: only, instructions: "answer the caller" }
        entries:
          main:
            agent: only
        """;

        private const string ClockOffYaml = """
        apiVersion: agentcore/v1
        agents:
          items:
            - { id: only, instructions: "answer the caller", clock: false }
        entries:
          main:
            agent: only
        """;

        // RunningLiveCall's clock stands at 2026-10-04 12:00 UTC, a Sunday.
        [Fact(Timeout = 30_000)]
        public async Task TheCallTellsGptLiveTheDateAndTimeBeforeTheGreeting()
        {
            await using RunningLiveCall running = await RunningLiveCall.StartAsync(new StallOnCueChatClient(), [], ClockOnYaml);

            _ = await running.Sideband.WaitForSentAsync(IsGreeting);

            JsonObject clock = running.Sideband.Sent[0];
            Assert.Equal("session.instructions.append", (string?)clock["type"]);
            Assert.True(clock.ContainsKey("delegation_id"));
            Assert.Null(clock["delegation_id"]);
            Assert.Equal("Today is Sunday, 2026-10-04. The local time is 12:00, UTC (UTC+00:00).", (string?)clock["content"]);
            Assert.True(IsGreeting(running.Sideband.Sent[1]));
            Assert.NotEqual((string?)clock["event_id"], (string?)running.Sideband.Sent[1]["event_id"]);
        }

        [Fact(Timeout = 30_000)]
        public async Task AnEntryAgentWithClockOffTellsGptLiveNoTime()
        {
            await using RunningLiveCall running = await RunningLiveCall.StartAsync(new StallOnCueChatClient(), [], ClockOffYaml);

            JsonObject greeting = await running.Sideband.WaitForSentAsync(IsGreeting);

            Assert.Same(greeting, running.Sideband.Sent[0]);
        }

        private static bool IsGreeting(JsonObject sent)
        {
            return (string?)sent["content"] == RunningLiveCall.Greeting;
        }
    }
}
