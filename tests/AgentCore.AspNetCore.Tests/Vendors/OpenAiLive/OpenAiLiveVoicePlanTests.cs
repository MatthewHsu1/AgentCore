using System.Text.Json.Nodes;
using AgentCore.Application.Conversation.Commands;
using AgentCore.Application.Tools.Builtin;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.AspNetCore.Vendors.OpenAiLive.Wire;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Vendors.OpenAiLive
{
    /// <summary>
    /// The backend steers GPT-Live with plans: silent context that names the next step. GPT-Live cannot delete an
    /// append, so each plan is numbered and says it replaces the earlier ones (docs/probes/live-roadmap: no stale step
    /// in 20 calls with that line).
    /// </summary>
    public sealed class OpenAiLiveVoicePlanTests
    {
        private const string First = "Next: ask which treadmill model they have.\n\"Then\" ask for the serial number.";

        private const string Second = "Next: ask whether they ever lubricated the belt.";

        private const string Third = "Next: ask which day this week suits them for a technician visit.";

        private const string PlanYaml = """
        apiVersion: agentcore/v1
        tools:
          - { id: plan, kind: builtin, uses: voice.plan, description: "Set the voice's plan." }
        agents:
          defaults: { clock: false }
          items:
            - { id: only, instructions: "answer the caller", tools: [ plan ] }
        entries:
          main:
            agent: only
        """;

        [Fact(Timeout = 30_000)]
        public async Task APlanGoesOutAsSilentSessionContextUnderItsNumber()
        {
            await using RunningLiveCall running = await RunningLiveCall.StartAsync(new StallOnCueChatClient(), []);
            _ = await running.Sideband.WaitForSentAsync(IsGreeting);

            ChannelCommandResult result = running.Call.Session.Send(new SetVoicePlanCommand(First));
            JsonObject plan = await running.Sideband.WaitForSentAsync(IsPlan);

            Assert.Equal(ChannelCommandResult.Scheduled, result);
            Assert.Null(plan["delegation_id"]);
            Assert.Equal("Plan 1. This replaces every earlier plan.\n" + First, (string?)plan["content"]);
        }

        [Fact(Timeout = 30_000)]
        public async Task EachPlanTakesTheNextNumber()
        {
            await using RunningLiveCall running = await RunningLiveCall.StartAsync(new StallOnCueChatClient(), []);
            _ = await running.Sideband.WaitForSentAsync(IsGreeting);

            _ = running.Call.Session.Send(new SetVoicePlanCommand(First));
            _ = running.Call.Session.Send(new SetVoicePlanCommand(Second));
            await running.Sideband.WaitForDrainAsync();

            Assert.Equal(
                ["Plan 1. This replaces every earlier plan.\n" + First, "Plan 2. This replaces every earlier plan.\n" + Second],
                PlansSent(running));
        }

        // The guide sends nothing before session.started; plans sent earlier keep their order and numbers.
        [Fact(Timeout = 30_000)]
        public async Task PlansSentBeforeTheSessionStartedWaitThenGoOutInOrder()
        {
            await using RunningLiveCall running = await RunningLiveCall.StartAsync(new StallOnCueChatClient(), [], started: false);
            _ = running.Call.Session.Send(new SetVoicePlanCommand(First));
            _ = running.Call.Session.Send(new SetVoicePlanCommand(Second));
            await running.Sideband.WaitForDrainAsync();
            bool sentBeforeTheStart = running.Sideband.Sent.Count > 0;

            running.Sideband.Push([RunningLiveCall.Started]);
            _ = await running.Sideband.WaitForSentAsync(IsGreeting);

            Assert.False(sentBeforeTheStart);
            Assert.Equal(
                ["Plan 1. This replaces every earlier plan.\n" + First, "Plan 2. This replaces every earlier plan.\n" + Second],
                PlansSent(running));
        }

        // A plan sent while the waiting plans are still going out must queue behind them: if Plan 3 went out before
        // Plan 2, the voice would read "Plan 2. This replaces every earlier plan." last and follow a stale plan.
        [Fact(Timeout = 30_000)]
        public async Task APlanSentWhileWaitingPlansGoOutQueuesBehindThem()
        {
            TaskCompletionSource socket = new(TaskCreationOptions.RunContinuationsAsynchronously);
            FakeSideband sideband = new() { SendsHeldUntil = socket.Task };
            await using RunningLiveCall running = await RunningLiveCall.StartAsync(new StallOnCueChatClient(), [], sideband: sideband, started: false);
            _ = running.Call.Session.Send(new SetVoicePlanCommand(First));
            _ = running.Call.Session.Send(new SetVoicePlanCommand(Second));

            running.Sideband.Push([RunningLiveCall.Started]);
            _ = await running.Sideband.WaitForSentAsync(IsPlan);
            _ = running.Call.Session.Send(new SetVoicePlanCommand(Third));
            socket.SetResult();
            _ = await running.Sideband.WaitForSentAsync(IsGreeting);
            await running.Sideband.WaitForDrainAsync();

            Assert.Equal(
                [
                    "Plan 1. This replaces every earlier plan.\n" + First,
                    "Plan 2. This replaces every earlier plan.\n" + Second,
                    "Plan 3. This replaces every earlier plan.\n" + Third,
                ],
                PlansSent(running));
        }

        // The plan goes out while the backend runs and the answer after it, so the voice reads the plan first
        // (spec "Flow"). The probe sent the plan 0.2 s before the commentary.
        [Fact(Timeout = 30_000)]
        public async Task ABackendThatCallsVoicePlanReachesTheVoiceBeforeItsAnswer()
        {
            GatedToolCallingChatClient model = new(new Dictionary<string, object?>(StringComparer.Ordinal) { ["plan"] = First });
            await using RunningLiveCall running = await RunningLiveCall.StartAsync(
                model, [], yaml: PlanYaml, tools: tool => new VoicePlanToolDefinition().Build(tool, new BuiltinToolPorts(ChatClients: null)));
            _ = await running.Sideband.WaitForSentAsync(IsGreeting);

            running.Sideband.Push(Ask("item_1", "My treadmill belt keeps slipping.", 1000));
            _ = await running.Sideband.WaitForSentAsync(IsCommentary);

            List<JsonObject> sent = [.. running.Sideband.Sent];
            int plan = sent.FindIndex(IsPlan);
            Assert.Equal("Plan 1. This replaces every earlier plan.\n" + First, (string?)sent[plan]["content"]);
            Assert.InRange(plan, 0, sent.FindIndex(IsCommentary) - 1);
        }

        private static List<string?> PlansSent(RunningLiveCall running)
        {
            return [.. running.Sideband.Sent.Where(IsPlan).Select(sent => (string?)sent["content"])];
        }

        private static bool IsGreeting(JsonObject sent)
        {
            return (string?)sent["content"] == RunningLiveCall.Greeting;
        }

        private static bool IsCommentary(JsonObject sent)
        {
            return (string?)sent["type"] == "session.commentary.append" && (string?)sent["delegation_id"] == "item_1";
        }

        private static string[] Ask(string delegationId, string words, int startMs)
        {
            return [
                new JsonObject { ["type"] = OpenAiLiveEvents.InputTranscriptDelta, ["delta"] = " " + words, ["start_ms"] = startMs, ["end_ms"] = startMs + 1000 }.ToJsonString(),
                new JsonObject { ["type"] = OpenAiLiveEvents.DelegationCreated, ["delegation"] = new JsonObject { ["id"] = delegationId } }.ToJsonString(),
            ];
        }

        private static bool IsPlan(JsonObject sent)
        {
            return (string?)sent["type"] == "session.thinking.append"
                && ((string?)sent["content"])?.StartsWith("Plan ", StringComparison.Ordinal) == true;
        }
    }
}
