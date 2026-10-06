using System.Text.Json.Nodes;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.AspNetCore.Vendors.OpenAiLive.Wire;

namespace AgentCore.AspNetCore.Tests.Vendors.OpenAiLive
{
    /// <summary>
    /// What the GPT-Live call tests share. After the loop ends the call's session is closed, so these tests wait for
    /// ConversationEnded instead of flushing: a hook's notices arrive in order, so every LineSpoken raised before the
    /// end is already recorded.
    /// </summary>
    internal static class LiveSent
    {
        // The agent's policy reaches a terminal stage after its first turn, so the engine ends the conversation
        // inside the first ask (as HookSessions.EndsAfterTheFirstTurnYaml does).
        internal const string EndsAfterTheFirstAskYaml = """
        apiVersion: agentcore/v1
        guards:
          afterFirst: { ">=": [ { var: turnIndex }, 1 ] }
        agents:
          defaults: { clock: false }
          items:
            - { id: only, instructions: "answer the caller" }
        entries:
          main:
            policy:
              initial: working
              stages:
                - { id: working, agent: only, to: [ { stage: done, when: afterFirst } ] }
                - { id: done, agent: only, terminal: true }
        """;

        // Every call opens with the session instructions and the greeting (see TheCallTellsGptLiveToGreetTheCallerFirst).
        internal static IEnumerable<JsonObject> AfterTheGreeting(RunningLiveCall running) =>
            running.Sideband.Sent.Where(sent => (string?)sent["type"] != "session.instructions.append" && !IsGreeting(sent));

        // An answer's commentary names its delegation; the greeting's names none.
        internal static bool IsCommentary(JsonObject sent) => (string?)sent["type"] == OpenAiLiveEvents.CommentaryAppend && sent["delegation_id"] is not null;

        private static bool IsGreeting(JsonObject sent) => (string?)sent["type"] == OpenAiLiveEvents.CommentaryAppend && sent["delegation_id"] is null;

        internal static string? TypeOf(string json) => (string?)JsonNode.Parse(json)!["type"];

        internal static async Task PushAndDrainAsync(RunningLiveCall running, IEnumerable<string> events)
        {
            running.Sideband.Push(events);
            await running.Sideband.WaitForDrainAsync();
        }
    }
}
