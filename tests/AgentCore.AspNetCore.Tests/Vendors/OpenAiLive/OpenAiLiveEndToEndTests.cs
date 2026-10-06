using System.Text.Json.Nodes;
using AgentCore.Application.Hooks.BuiltIn;
using AgentCore.Application.Hooks.Notices;
using AgentCore.Application.Secrets;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.AspNetCore.Vendors.OpenAiLive.Wire;
using AgentCore.Domain.Audit;
using AgentCore.TestSupport;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Vendors.OpenAiLive
{
    /// <summary>One recorded call, through the real route, the real call core, and the real engine.</summary>
    public sealed class OpenAiLiveEndToEndTests
    {
        [Fact(Timeout = 30_000)]
        public async Task TheRecordedP1CallRunsThroughTheRealRoute()
        {
            RecordingHook hook = new();
            InstructionsSeenChatClient model = new(new StallOnCueChatClient());
            await using OpenAiLiveHost host = await OpenAiLiveHost.StartAsync(
                model, [hook, new CallDecider(gate => gate.Accept("cw_1_call_" + gate.CallId, "BRIEF-P1"))]);
            _ = await host.PostWebhookAsync(OpenAiLiveHost.IncomingCall("rtc_p1"));
            FakeSideband sideband = await host.FirstAttach.Task;
            IReadOnlyList<string> log = LiveLog.Inbound("p1-a");
            int delegation = LiveLog.IndexOf(log, OpenAiLiveEvents.DelegationCreated);

            sideband.Push(log.Take(delegation + 1));
            JsonObject commentary = await sideband.WaitForSentAsync(LiveSent.IsCommentary);
            sideband.Push(log.Skip(delegation + 1));
            ConversationEnded ended = await hook.WaitForAsync<ConversationEnded>();

            Assert.Equal("reply to Can you check the status of my order? The order number is A four four seven one", (string?)commentary["content"]);
            Assert.Contains("BRIEF-P1", model.Instructions[^1], StringComparison.Ordinal);
            Assert.Contains(CallBriefHook.FrontVoiceNote, model.Instructions[^1], StringComparison.Ordinal);
            Assert.Equal(
                [
                    (Speaker.Caller, "Hi there"),
                    (Speaker.Agent, "Hey! Thanks for calling Sole Fitness. How can I help?"),
                    (Speaker.Caller, "Can you check the status of my order? The order number is A four four seven one"),
                    (Speaker.Agent, "Sure. Checking that now. Okay. That order shipped on September 24 with UPS. It should arrive on Tuesday, September 29."),
                    (Speaker.Caller, "Okay. Thanks. That's all"),
                    (Speaker.Agent, "You're welcome. Take care!"),
                ],
                hook.Of<LineSpoken>().Select(line => (line.Speaker, line.Text)));
            Assert.Equal(("cw_1_call_rtc_p1", ConversationEndReason.CallerHungUp), (ended.Scope.ConversationId, ended.Reason));
            Assert.Equal(("rtc_p1", "close_requested"), (ended.Call!.CallId, ended.Call.Cause));
        }

        // No call outlives the host.
        [Fact(Timeout = 30_000)]
        public async Task AShutdownHangsUpEveryLiveCallAndEndsEachWithCauseShutdown()
        {
            RecordingHook hook = new();
            await using OpenAiLiveHost host = await OpenAiLiveHost.StartAsync(new StallOnCueChatClient(), [hook]);
            _ = await host.PostWebhookAsync(OpenAiLiveHost.IncomingCall("rtc_a"), webhookId: "wh_a");
            _ = await host.PostWebhookAsync(OpenAiLiveHost.IncomingCall("rtc_b"), webhookId: "wh_b");
            await Poll.UntilAsync(() =>
            {
                lock (host.Attached)
                {
                    return host.Attached.Count == 2;
                }
            });

            await host.StopAsync();
            ConversationEnded first = await hook.WaitForAsync<ConversationEnded>(ended => ended.Call!.CallId == "rtc_a");
            ConversationEnded second = await hook.WaitForAsync<ConversationEnded>(ended => ended.Call!.CallId == "rtc_b");

            Assert.Equal(["/v1/live/sessions/rtc_a/hangup", "/v1/live/sessions/rtc_b/hangup"], host.Control.Requests.Select(seen => seen.Path).Where(path => path.EndsWith("/hangup", StringComparison.Ordinal)).Order());
            Assert.Equal(
                [(ConversationEndReason.Faulted, "shutdown"), (ConversationEndReason.Faulted, "shutdown")],
                [(first.Reason, first.Call!.Cause), (second.Reason, second.Call!.Cause)]);
            Assert.NotEqual(first.Scope.ConversationId, second.Scope.ConversationId);
        }

        // A missing webhook secret stops the host, not the first caller.
        [Fact(Timeout = 30_000)]
        public async Task AHostWithNoWebhookSecretFailsToStart()
        {
            if (Environment.GetEnvironmentVariable(KnownSecrets.OpenAiWebhookSecretVariable) is not null)
            {
                Assert.Skip("OPENAI_WEBHOOK_SECRET is set in this environment, so the resolver's fallback finds a secret.");
            }

            SecretResolutionException failure = await Assert.ThrowsAsync<SecretResolutionException>(
                () => OpenAiLiveHost.StartAsync(new StallOnCueChatClient(), [], new MapSecretResolver().With("openai-api-key", OpenAiLiveHost.ApiKey)));

            Assert.Contains("openai-webhook-secret", failure.Message, StringComparison.Ordinal);
        }
    }
}
