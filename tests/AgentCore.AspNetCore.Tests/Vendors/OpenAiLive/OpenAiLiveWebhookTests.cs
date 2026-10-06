using System.Net;
using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Hooks.Gates;
using AgentCore.Application.Hooks.Notices;
using AgentCore.AspNetCore.DependencyInjection;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.AspNetCore.Voice.Routing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using AgentCore.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Vendors.OpenAiLive
{
    /// <summary>The incoming-call webhook through the real /v1/{entry}/call route.</summary>
    public sealed class OpenAiLiveWebhookTests
    {
        private const string Brief = "Earlier summary, for context only.\n\"Ask first.\"";

        [Fact(Timeout = 30_000)]
        public async Task AnAcceptedCallIsAcceptedWithItsInstructionsVoiceAndClientDelegationThenAttached()
        {
            RecordingHook hook = new();
            await using OpenAiLiveHost host = await OpenAiLiveHost.StartAsync(
                new StallOnCueChatClient(), [hook, new CallDecider(gate => gate.Accept("cw_1_call_" + gate.CallId, Brief))]);

            HttpResponseMessage response = await host.PostWebhookAsync(OpenAiLiveHost.IncomingCall("rtc_123"));
            FakeSideband sideband = await host.FirstAttach.Task;
            JsonObject greet = await sideband.WaitForSentAsync(sent => (string?)sent["type"] == "session.commentary.append");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            RecordingLiveControl.Seen accept = Assert.Single(host.Control.Requests);
            Assert.Equal(("POST", "/v1/live/sessions/rtc_123/accept", "Bearer sk-test"), (accept.Method, accept.Path, accept.Authorization));
            // A real call refused anything else: "Invalid Live SIP accept payload: session must be the only field."
            Assert.Equal(["session"], accept.Body!.AsObject().Select(field => field.Key));
            JsonNode session = accept.Body["session"]!;
            Assert.Equal("Be brief.\n\n" + Brief, (string?)session["instructions"]);
            Assert.Equal("marin", (string?)session["audio"]!["output"]!["voice"]);
            Assert.Equal("gpt-live-1", (string?)session["model"]);
            Assert.Equal("client", (string?)session["delegation"]!["type"]);
            Assert.Equal("Say hello.", (string?)greet["content"]);
            CallStarted started = await hook.WaitForAsync<CallStarted>();
            Assert.Equal(("rtc_123", "+15550100", "openai-live", "cw_1_call_rtc_123"), (started.CallId, started.From, started.Transport, started.Scope.ConversationId));
        }

        [Fact(Timeout = 30_000)]
        public async Task ARejectingHookRejectsTheCallWithItsSipCode()
        {
            await using OpenAiLiveHost host = await OpenAiLiveHost.StartAsync(new StallOnCueChatClient(), [new CallDecider(gate => gate.Reject(CallRefusal.Busy))]);

            HttpResponseMessage response = await host.PostWebhookAsync(OpenAiLiveHost.IncomingCall("rtc_123"));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            RecordingLiveControl.Seen reject = Assert.Single(host.Control.Requests);
            Assert.Equal("/v1/live/sessions/rtc_123/reject", reject.Path);
            Assert.Equal(486, (int?)reject.Body!["status_code"]);
            Assert.Empty(host.Attached);
        }

        // A hook that throws fails closed.
        [Fact(Timeout = 30_000)]
        public async Task AHookThatThrowsRejectsTheCallAsUnavailable()
        {
            await using OpenAiLiveHost host = await OpenAiLiveHost.StartAsync(new StallOnCueChatClient(), [new CallDecider(_ => throw new InvalidOperationException("crm down"))]);

            _ = await host.PostWebhookAsync(OpenAiLiveHost.IncomingCall("rtc_123"));

            Assert.Equal(503, (int?)Assert.Single(host.Control.Requests).Body!["status_code"]);
        }

        [Fact(Timeout = 30_000)]
        public async Task ABadSignatureIsRefusedBeforeAnyHookRuns()
        {
            bool asked = false;
            EntryCounter entries = new();
            await using OpenAiLiveHost host = await OpenAiLiveHost.StartAsync(new StallOnCueChatClient(), [entries, new CallDecider(_ => asked = true)]);

            HttpResponseMessage response = await host.PostWebhookAsync(OpenAiLiveHost.IncomingCall("rtc_123"), secret: "whsec_" + Convert.ToBase64String(new byte[24]));

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.False(asked);
            Assert.Equal(0, entries.Count);
            Assert.Empty(host.Control.Requests);
        }

        // A host's sign-in middleware finds the scheme only through IAuthorizeData (docs/probes/call-route-auth).
        [Fact(Timeout = 30_000)]
        public async Task TheCallRouteNamesTheCallerSchemeWhereAHostsSignInCanReadIt()
        {
            await using OpenAiLiveHost host = await OpenAiLiveHost.StartAsync(new StallOnCueChatClient(), []);

            RouteEndpoint call = host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
                .Single(endpoint => endpoint.RoutePattern.RawText == ConversationEndpointRouteBuilderExtensions.DefaultPattern);

            Assert.Equal(
                [ConversationEndpointRouteBuilderExtensions.CallerScheme],
                call.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(data => data.AuthenticationSchemes));
        }

        [Fact(Timeout = 30_000)]
        public async Task AStaleTimestampIsRefused()
        {
            await using OpenAiLiveHost host = await OpenAiLiveHost.StartAsync(new StallOnCueChatClient(), []);

            HttpResponseMessage response = await host.PostWebhookAsync(OpenAiLiveHost.IncomingCall("rtc_123"), signedAt: DateTimeOffset.UtcNow.AddMinutes(-6));

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        // One process never admits one call twice.
        [Fact(Timeout = 30_000)]
        public async Task ARepeatedWebhookForTheSameCallIsAcceptedOnce()
        {
            await using OpenAiLiveHost host = await OpenAiLiveHost.StartAsync(new StallOnCueChatClient(), []);

            _ = await host.PostWebhookAsync(OpenAiLiveHost.IncomingCall("rtc_123"), webhookId: "wh_1");
            _ = await host.FirstAttach.Task;
            HttpResponseMessage repeated = await host.PostWebhookAsync(OpenAiLiveHost.IncomingCall("rtc_123"), webhookId: "wh_1");
            HttpResponseMessage redelivered = await host.PostWebhookAsync(OpenAiLiveHost.IncomingCall("rtc_123"), webhookId: "wh_2");

            Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK), (repeated.StatusCode, redelivered.StatusCode));
            Assert.Single(host.Control.Requests);
            Assert.Single(host.Attached);
        }

        [Fact(Timeout = 30_000)]
        public async Task AnEventThatIsNotAnIncomingCallIsAcknowledgedAndIgnored()
        {
            await using OpenAiLiveHost host = await OpenAiLiveHost.StartAsync(new StallOnCueChatClient(), []);

            HttpResponseMessage response = await host.PostWebhookAsync("""{"type":"realtime.call.ended","data":{"call_id":"rtc_123"}}""");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Empty(host.Control.Requests);
        }

        // Another process already answered; this one lets go with no call notice.
        [Fact(Timeout = 30_000)]
        public async Task ADecisionAlreadyMadeLetsTheCallGoWithNoCallNotice()
        {
            RecordingHook hook = new();
            await using OpenAiLiveHost host = await OpenAiLiveHost.StartAsync(new StallOnCueChatClient(), [hook]);
            host.Control.Answer = path => path.EndsWith("/accept", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("""{"error":{"code":"decision_already_made"}}""") }
                : null;

            HttpResponseMessage response = await host.PostWebhookAsync(OpenAiLiveHost.IncomingCall("rtc_123"));
            await host.Services.GetRequiredService<AgentCoreBoot>().Hooks.Notices.FlushAsync("rtc_123");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Empty(host.Attached);
            Assert.Empty(hook.Of<CallStarted>());
            Assert.Empty(hook.Of<ConversationStarted>());
        }

        // Every admitted call reaches StartAsync or AbandonAsync: a call whose accept never lands frees its conversation.
        [Theory(Timeout = 30_000)]
        [InlineData(false)]
        [InlineData(true)]
        public async Task AnAcceptThatFailsFreesTheConversationForTheNextCall(bool throwing)
        {
            await using OpenAiLiveHost host = await OpenAiLiveHost.StartAsync(
                new StallOnCueChatClient(), [new CallDecider(gate => gate.Accept("thread-9"))]);
            host.Control.Answer = path => path == "/v1/live/sessions/rtc_1/accept"
                ? throwing ? throw new TaskCanceledException("the deadline passed") : new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : null;

            _ = await host.PostWebhookAsync(OpenAiLiveHost.IncomingCall("rtc_1"), webhookId: "wh_1");
            HttpResponseMessage second = await host.PostWebhookAsync(OpenAiLiveHost.IncomingCall("rtc_2"), webhookId: "wh_2");
            _ = await host.FirstAttach.Task;

            Assert.Equal(HttpStatusCode.OK, second.StatusCode);
            Assert.Equal(
                ["/v1/live/sessions/rtc_1/accept", "/v1/live/sessions/rtc_2/accept"],
                host.Control.Requests.Select(seen => seen.Path).Where(path => path.EndsWith("/accept", StringComparison.Ordinal)));
            Assert.Single(host.Attached);

            // A thrown accept is unknown, so the call is hung up; a refused one is rejected, so the caller is not left ringing.
            RecordingLiveControl.Seen[] first = [.. host.Control.Requests.Where(seen => seen.Path.StartsWith("/v1/live/sessions/rtc_1/", StringComparison.Ordinal) && !seen.Path.EndsWith("/accept", StringComparison.Ordinal))];
            Assert.Equal(throwing ? "/v1/live/sessions/rtc_1/hangup" : "/v1/live/sessions/rtc_1/reject", Assert.Single(first).Path);
            if (!throwing)
            {
                Assert.Equal(503, (int?)first[0].Body!["status_code"]);
            }
        }

        // The adapter reads the live: block at boot, so a misspelt key fails the start and not the first caller.
        [Fact(Timeout = 30_000)]
        public async Task AnUnknownLiveKeyFailsTheHostStart()
        {
            string yaml = OpenAiLiveHost.LiveYaml.Replace("instructions: \"Be brief.\"", "instructions: \"Be brief.\"\n      webhookSecret: x", StringComparison.Ordinal);

            ConfigurationLoadException failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
                () => OpenAiLiveHost.StartAsync(new StallOnCueChatClient(), [], yaml: yaml));

            Assert.Contains("webhookSecret", failure.Message, StringComparison.Ordinal);
        }
    }
}
