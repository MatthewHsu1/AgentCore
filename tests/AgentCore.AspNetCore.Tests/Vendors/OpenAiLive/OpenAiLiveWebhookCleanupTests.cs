using System.Net;
using AgentCore.Application.Hooks.Notices;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.AspNetCore.Vendors.OpenAiLive;
using AgentCore.AspNetCore.Vendors.OpenAiLive.Call;
using AgentCore.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Vendors.OpenAiLive
{
    /// <summary>What the webhook and the registry leave behind when a call faults part way: no claimed conversation, no live call, no remembered webhook id.</summary>
    public sealed class OpenAiLiveWebhookCleanupTests
    {
        private const string Thread = "thread-9";

        private static bool Hangup(RecordingLiveControl.Seen seen, string callId) => seen.Path == $"/v1/live/sessions/{callId}/hangup";

        // The attach is the last step before the call runs.
        [Fact(Timeout = 30_000)]
        public async Task AnAttachThatFailsHangsUpEndsTheCallAndFreesItsConversation()
        {
            RecordingHook hook = new();
            await using OpenAiLiveHost host = await OpenAiLiveHost.StartAsync(
                new StallOnCueChatClient(),
                [hook, new CallDecider(gate => gate.Accept(Thread))],
                attachFault: attach => attach.CallId == "rtc_1" ? new IOException("no sideband") : null);

            _ = await host.PostWebhookAsync(OpenAiLiveHost.IncomingCall("rtc_1"), webhookId: "wh_1");
            ConversationEnded ended = await hook.WaitForAsync<ConversationEnded>();
            HttpResponseMessage second = await host.PostWebhookAsync(OpenAiLiveHost.IncomingCall("rtc_2"), webhookId: "wh_2");
            _ = await host.FirstAttach.Task;

            Assert.Contains(host.Control.Requests, seen => Hangup(seen, "rtc_1"));
            Assert.Equal(("rtc_1", OpenAiLiveCall.AttachFailedCause), (ended.Call!.CallId, ended.Call.Cause));
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
            Assert.Single(host.Attached);
        }

        // StartAsync opens the store after the accept landed: a store that is down must not leave GPT-Live a call with no sideband.
        [Fact(Timeout = 30_000)]
        public async Task AStartThatFailsAfterTheAcceptHangsUpAndFreesTheConversation()
        {
            FailingCreateStore store = new();
            string yaml = OpenAiLiveHost.LiveYaml.Replace("  llm:\n", "  conversations: { kind: test }\n  llm:\n", StringComparison.Ordinal);
            await using OpenAiLiveHost host = await OpenAiLiveHost.StartAsync(
                new StallOnCueChatClient(),
                [new CallDecider(gate => gate.Accept(Thread))],
                yaml: yaml,
                configure: options => options.UseConversationStores(store));

            HttpResponseMessage first = await host.PostWebhookAsync(OpenAiLiveHost.IncomingCall("rtc_1"), webhookId: "wh_1");
            store.Failing = false;
            HttpResponseMessage second = await host.PostWebhookAsync(OpenAiLiveHost.IncomingCall("rtc_2"), webhookId: "wh_2");
            _ = await host.FirstAttach.Task;

            Assert.Equal(HttpStatusCode.InternalServerError, first.StatusCode);
            Assert.Contains(host.Control.Requests, seen => Hangup(seen, "rtc_1"));
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
            Assert.Single(host.Attached);
        }

        // A webhook id counts as handled only after a 2xx; OpenAI redelivers a 500 under the same id.
        [Fact(Timeout = 30_000)]
        public async Task ARequestThatEndsIn500LetsTheRedeliveryUnderTheSameWebhookIdThrough()
        {
            RecordingHook hook = new();
            await using OpenAiLiveHost host = await OpenAiLiveHost.StartAsync(new StallOnCueChatClient(), [hook]);
            int accepts = 0;
            host.Control.Answer = path => path.EndsWith("/accept", StringComparison.Ordinal) && Interlocked.Increment(ref accepts) == 1
                ? throw new TaskCanceledException("the deadline passed")
                : null;

            HttpResponseMessage first = await host.PostWebhookAsync(OpenAiLiveHost.IncomingCall("rtc_1"), webhookId: "wh_1");
            HttpResponseMessage redelivered = await host.PostWebhookAsync(OpenAiLiveHost.IncomingCall("rtc_1"), webhookId: "wh_1");
            _ = await host.FirstAttach.Task;
            CallStarted started = await hook.WaitForAsync<CallStarted>();

            Assert.Equal((HttpStatusCode.InternalServerError, HttpStatusCode.OK), (first.StatusCode, redelivered.StatusCode));
            Assert.Equal("rtc_1", started.CallId);
            Assert.Single(host.Attached);
        }

        // A host token that is already spent must not skip the end of a call or leave its loop reading.
        [Fact(Timeout = 30_000)]
        public async Task AStopWithASpentTokenStillEndsEveryRunningCallAndStopsItsLoop()
        {
            RecordingHook hook = new();
            await using OpenAiLiveHost host = await OpenAiLiveHost.StartAsync(new StallOnCueChatClient(), [hook]);
            _ = await host.PostWebhookAsync(OpenAiLiveHost.IncomingCall("rtc_a"), webhookId: "wh_a");
            _ = await host.PostWebhookAsync(OpenAiLiveHost.IncomingCall("rtc_b"), webhookId: "wh_b");

            Exception? stopped = await Record.ExceptionAsync(
                () => host.Services.GetRequiredService<OpenAiLiveCalls>().StopAsync(new CancellationToken(canceled: true)));
            ConversationEnded endedA = await hook.WaitForAsync<ConversationEnded>(notice => notice.Call?.CallId == "rtc_a");
            ConversationEnded endedB = await hook.WaitForAsync<ConversationEnded>(notice => notice.Call?.CallId == "rtc_b");
            await Poll.UntilAsync(() => host.Attached.All(sideband => sideband.Disposed));

            Assert.True(stopped is null or OperationCanceledException);
            Assert.Equal(OpenAiLiveCall.ShutdownCause, endedA.Call!.Cause);
            Assert.Equal(OpenAiLiveCall.ShutdownCause, endedB.Call!.Cause);
            Assert.Contains(host.Control.Requests, seen => Hangup(seen, "rtc_a"));
            Assert.Contains(host.Control.Requests, seen => Hangup(seen, "rtc_b"));
        }

        // A call that reaches the registry after the host began to stop is ended, not run.
        [Fact(Timeout = 30_000)]
        public async Task ACallThatArrivesAfterTheStopBeganIsHungUpAndEndedWithCauseShutdown()
        {
            RecordingHook hook = new();
            using SemaphoreSlim attaching = new(0);
            using SemaphoreSlim proceed = new(0);
            await using OpenAiLiveHost host = await OpenAiLiveHost.StartAsync(
                new StallOnCueChatClient(),
                [hook],
                attachFault: offered =>
                {
                    _ = attaching.Release();
                    _ = proceed.Wait(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
                    return null;
                });

            Task<HttpResponseMessage> posting = host.PostWebhookAsync(OpenAiLiveHost.IncomingCall("rtc_late"));
            await attaching.WaitAsync(TestContext.Current.CancellationToken);
            await host.Services.GetRequiredService<OpenAiLiveCalls>().StopAsync(TestContext.Current.CancellationToken);
            _ = proceed.Release();
            _ = await posting;
            ConversationEnded ended = await hook.WaitForAsync<ConversationEnded>();
            await Poll.UntilAsync(() => host.Attached.All(sideband => sideband.Disposed));

            Assert.Equal(OpenAiLiveCall.ShutdownCause, ended.Call!.Cause);
            Assert.Contains(host.Control.Requests, seen => Hangup(seen, "rtc_late"));
        }
    }
}
