using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Conversation.Actions;
using AgentCore.Application.Hooks.Notices;
using AgentCore.Application.Tools;
using AgentCore.Application.Tools.Binding;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.AspNetCore.Vendors.OpenAiLive;
using AgentCore.AspNetCore.Vendors.OpenAiLive.Call;
using AgentCore.AspNetCore.Vendors.OpenAiLive.Wire;
using AgentCore.Domain.Audit;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;
using static AgentCore.AspNetCore.Tests.Vendors.OpenAiLive.LiveSent;

namespace AgentCore.AspNetCore.Tests.Vendors.OpenAiLive
{
    /// <summary>
    /// A tool asks for a transfer through its scope. Probe T1 (docs/probes/live-transfer-t1) set the rules: OpenAI never
    /// reports the outcome, so the peer's close of the AI leg is the only success signal.
    /// </summary>
    public sealed class OpenAiLiveTransferTests
    {
        private static readonly Uri Staff = new("tel:+15550002222");

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        // GPT-Live's ack marks the start of speech, so time alone must not send the refer before the answer was heard.
        [Fact(Timeout = 30_000)]
        public async Task TheReferWaitsUntilTheAnswerWasHeardAndACloseEndsTheCallAsTransferred()
        {
            RecordingHook hook = new();
            ConcurrentQueue<ConversationActionResult> answers = new();
            await using RunningLiveCall running = await RunningLiveCall.StartAsync(
                new GatedToolCallingChatClient(), [hook], tools: TransferTool(answers), refers: true);

            running.Sideband.Push(Ask("item_1", "Can I talk to a person?", 1000));
            JsonObject answer = await running.Sideband.WaitForSentAsync(IsCommentary);
            await running.Time.WaitForTimersAsync(RunningLiveCall.Noon + OpenAiLiveCall.AckWait, 1);
            running.Time.Advance(OpenAiLiveCall.EndQuietWait);
            bool referredBeforeSpeechStarted = running.Referred.Task.IsCompleted;
            running.Sideband.Ack(answer);
            await running.Sideband.WaitForDrainAsync();
            running.Time.Advance(OpenAiLiveCall.EndQuietWait);
            Uri target = await running.Referred.Task;
            running.Sideband.Push([Heard("Hello?", 9000), Closed()]);
            await running.Loop;
            ConversationEnded ended = await hook.WaitForAsync<ConversationEnded>();

            Assert.Equal([ConversationActionResult.Scheduled], answers);
            Assert.False(referredBeforeSpeechStarted);
            Assert.Equal(Staff, target);
            Assert.False(running.HungUp.Task.IsCompleted);
            Assert.Equal((ConversationEndReason.TransferredToHuman, OpenAiLiveCall.TransferredCause), (ended.Reason, ended.Call!.Cause));
            Assert.Contains((Speaker.Caller, "Hello?"), hook.Of<LineSpoken>().Select(line => (line.Speaker, line.Text)));
        }

        // T1: a peer that declined the REFER still leaves OpenAI's answer at 200 and the session up.
        [Fact(Timeout = 30_000)]
        public async Task ACallStillUpWhenTheTransferWaitRunsOutIsHungUpAsAFailedTransfer()
        {
            RecordingHook hook = new();
            await using RunningLiveCall running = await RunningLiveCall.StartAsync(
                new GatedToolCallingChatClient(), [hook], tools: TransferTool(new()), refers: true);

            await ReferAsync(running);
            await running.Time.WaitForTimersAsync(running.Time.GetUtcNow() + OpenAiLiveSettings.DefaultTransferWait, 1);
            bool hungUpEarly = running.HungUp.Task.IsCompleted;
            running.Time.Advance(OpenAiLiveSettings.DefaultTransferWait);
            _ = await running.HungUp.Task;
            ConversationEnded ended = await hook.WaitForAsync<ConversationEnded>();

            Assert.False(hungUpEarly);
            Assert.Equal((ConversationEndReason.Faulted, OpenAiLiveCall.TransferFailedCause), (ended.Reason, ended.Call!.Cause));
        }

        [Fact(Timeout = 30_000)]
        public async Task ARefusedReferHangsUpAsAFailedTransfer()
        {
            RecordingHook hook = new();
            await using RunningLiveCall running = await RunningLiveCall.StartAsync(
                new GatedToolCallingChatClient(), [hook], tools: TransferTool(new()), refers: false);

            await ReferAsync(running);
            _ = await running.HungUp.Task;
            ConversationEnded ended = await hook.WaitForAsync<ConversationEnded>();

            Assert.Equal((ConversationEndReason.Faulted, OpenAiLiveCall.TransferFailedCause), (ended.Reason, ended.Call!.Cause));
        }

        // Once a transfer waits, the caller is leaving for the line: a question asked while the last answer is still
        // being heard must not start a turn of its own. An ask opens its turn while the loop handles the delegation,
        // and notices arrive in order, so a turn started for it would be recorded before the end.
        [Fact(Timeout = 30_000)]
        public async Task ADelegationWhileATransferWaitsStartsNoTurn()
        {
            RecordingHook hook = new();
            await using RunningLiveCall running = await RunningLiveCall.StartAsync(
                new GatedToolCallingChatClient(), [hook], tools: TransferTool(new()), refers: true);

            running.Sideband.Push(Ask("item_1", "Can I talk to a person?", 1000));
            running.Sideband.Ack(await running.Sideband.WaitForSentAsync(IsCommentary));
            await running.Sideband.WaitForDrainAsync();
            running.Sideband.Push(Ask("item_2", "Are you still there?", 9000));
            await running.Sideband.WaitForDrainAsync();
            running.Time.Advance(OpenAiLiveCall.EndQuietWait);
            _ = await running.Referred.Task;
            running.Sideband.Push([Closed()]);
            await running.Loop;
            _ = await hook.WaitForAsync<ConversationEnded>();

            Assert.Single(hook.Of<TurnStarted>());
        }

        [Fact(Timeout = 30_000)]
        public async Task ACallWithNoReferAnswersATransferAsNotSupportedAndStaysUp()
        {
            ConcurrentQueue<ConversationActionResult> answers = new();
            await using RunningLiveCall running = await RunningLiveCall.StartAsync(new GatedToolCallingChatClient(), [], tools: TransferTool(answers));

            running.Sideband.Push(Ask("item_1", "Can I talk to a person?", 1000));
            running.Sideband.Ack(await running.Sideband.WaitForSentAsync(IsCommentary));
            await running.Sideband.WaitForDrainAsync();
            running.Time.Advance(OpenAiLiveCall.EndQuietWait);

            Assert.Equal([ConversationActionResult.NotSupported], answers);
            Assert.False(running.Call.HasEnded);
        }

        // Every tool of the harness's document asks for a transfer through the scope the binder fills, as a host's would.
        private static Func<ToolConfiguration, AITool?> TransferTool(ConcurrentQueue<ConversationActionResult> answers) =>
            tool => AIFunctionFactory.Create(
                (ToolCallScope scope) =>
                {
                    ConversationActionResult answer = scope.Conversation.Request(new TransferAction(Staff));
                    answers.Enqueue(answer);
                    return answer.ToString();
                },
                new AIFunctionFactoryOptions { Name = tool.Id, Description = tool.Description, ConfigureParameterBinding = ToolParameterBindings.For });

        private static async Task ReferAsync(RunningLiveCall running)
        {
            running.Sideband.Push(Ask("item_1", "Can I talk to a person?", 1000));
            running.Sideband.Ack(await running.Sideband.WaitForSentAsync(IsCommentary));
            await running.Sideband.WaitForDrainAsync();
            running.Time.Advance(OpenAiLiveCall.EndQuietWait);
            _ = await running.Referred.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        }

        private static string[] Ask(string delegationId, string words, int startMs) =>
        [
            Heard(words, startMs),
            new JsonObject { ["type"] = OpenAiLiveEvents.DelegationCreated, ["delegation"] = new JsonObject { ["id"] = delegationId } }.ToJsonString(),
        ];

        private static string Heard(string words, int startMs) =>
            new JsonObject { ["type"] = OpenAiLiveEvents.InputTranscriptDelta, ["delta"] = " " + words, ["start_ms"] = startMs, ["end_ms"] = startMs + 1000 }.ToJsonString();

        private static string Closed() => new JsonObject { ["type"] = OpenAiLiveEvents.Closed, ["reason"] = "hangup" }.ToJsonString();
    }
}
