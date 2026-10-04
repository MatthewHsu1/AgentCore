using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Hooks.Gates;
using AgentCore.Application.Hooks.Layers;
using AgentCore.AspNetCore.Calls;
using AgentCore.AspNetCore.Vendors.OpenAiLive.Call;
using AgentCore.AspNetCore.Vendors.OpenAiLive.Wire;
using AgentCore.AspNetCore.Voice.Routing;
using AgentCore.Domain.Audit;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace AgentCore.AspNetCore.Vendors.OpenAiLive.Webhook
{
    /// <summary>The <c>/v1/{entry}/call</c> handler for GPT-Live's incoming-call webhook.</summary>
    internal sealed class OpenAiLiveWebhook(
        OpenAiLiveSettings settings,
        ConversationProviderConfiguration configuration,
        Task<OpenAiLiveCredentials> credentials,
        OpenAiLiveControl control,
        Func<LiveAttach, CancellationToken, ValueTask<ILiveSideband>> attach,
        Uri apiBase)
    {
        /// <summary>The largest webhook body read. An incoming-call event is about one kilobyte.</summary>
        internal const int MaxBodyBytes = 64 * 1024;

        internal async Task HandleAsync(HttpContext http)
        {
            if (!HttpMethods.IsPost(http.Request.Method))
            {
                http.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                return;
            }

            IServiceProvider services = http.RequestServices;
            ILogger logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("AgentCore.OpenAiLive");
            TimeProvider time = services.GetRequiredService<TimeProvider>();
            OpenAiLiveCalls calls = services.GetRequiredService<OpenAiLiveCalls>();
            OpenAiLiveCredentials keys = await credentials.ConfigureAwait(false);

            if (await ReadBodyAsync(http.Request, http.RequestAborted).ConfigureAwait(false) is not { } body)
            {
                http.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                return;
            }

            string? webhookId = Header(http, "webhook-id");
            if (!StandardWebhookSignature.Verify(keys.WebhookKey, webhookId, Header(http, "webhook-timestamp"), body, Header(http, "webhook-signature"), time.GetUtcNow()))
            {
                OpenAiLiveLog.SignatureRefused(logger);
                http.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            http.Response.StatusCode = StatusCodes.Status200OK;
            if (!calls.TryClaimWebhook(webhookId!, time.GetUtcNow())
                || OpenAiLiveWire.ReadIncomingCall(body) is not { } incoming
                || !calls.TryClaimCall(incoming.CallId))
            {
                return;
            }

            bool handed = false;
            try
            {
                handed = await AnswerAsync(http, incoming, keys, calls, logger).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The request ends in a 500, and OpenAI redelivers under the same webhook id.
                calls.ReleaseWebhook(webhookId!);
                throw;
            }
            finally
            {
                if (!handed)
                {
                    calls.ReleaseCall(incoming.CallId);
                }
            }
        }

        private async Task<bool> AnswerAsync(HttpContext http, LiveIncomingCall incoming, OpenAiLiveCredentials keys, OpenAiLiveCalls calls, ILogger logger)
        {
            CancellationToken stopping = calls.Stopping;
            PhoneCallHost host = PhoneCallHost.From(http.RequestServices, configuration) with { FrontVoice = true };
            CallOffer offer = new(
                ConversationEndpointRouteBuilderExtensions.EntryOf(http), incoming.CallId, incoming.From, incoming.To, incoming.Headers,
                OpenAiLiveConversationAdapter.OpenAiLiveKind);

            PhoneCallAdmission admission = await PhoneCall.AdmitAsync(host, offer, stopping).ConfigureAwait(false);
            if (admission.Call is not { } call)
            {
                if (!await control.RejectAsync(incoming.CallId, admission.Refusal ?? CallRefusal.Unavailable, stopping).ConfigureAwait(false))
                {
                    OpenAiLiveLog.RejectFailed(logger, incoming.CallId);
                }

                return false;
            }

            if (!await AcceptAndStartAsync(call, incoming.CallId, logger, stopping).ConfigureAwait(false))
            {
                return false;
            }

            ILiveSideband sideband;
            try
            {
                sideband = await attach(new LiveAttach(incoming.CallId, keys.ApiKey, apiBase), stopping).ConfigureAwait(false);
            }
            catch (Exception fault) when (fault is not OutOfMemoryException)
            {
                OpenAiLiveLog.AttachFailed(logger, incoming.CallId, fault);
                await HangUpAsync(incoming.CallId, logger).ConfigureAwait(false);
                await call.EndAsync(ConversationEndReason.Faulted, OpenAiLiveCall.AttachFailedCause).ConfigureAwait(false);
                await call.CloseAsync().ConfigureAwait(false);
                return false;
            }

            calls.Run(new OpenAiLiveCall(call, sideband, _ => control.HangupQuietlyAsync(incoming.CallId), logger));
            return true;
        }

        // Every admitted call reaches StartAsync or AbandonAsync, whatever the accept does: a call that never starts
        // would keep its conversation busy.
        private async Task<bool> AcceptAndStartAsync(PhoneCall call, string callId, ILogger logger, CancellationToken stopping)
        {
            bool started = false;
            try
            {
                if (!await AcceptedAsync(call, callId, logger, stopping).ConfigureAwait(false))
                {
                    return false;
                }

                await call.StartAsync(stopping).ConfigureAwait(false);
                started = true;
                return true;
            }
            catch (Exception)
            {
                // A thrown accept may still have landed, and a failed start follows an accept that did: either way
                // GPT-Live would be left with a call and no sideband.
                await HangUpAsync(callId, logger).ConfigureAwait(false);
                throw;
            }
            finally
            {
                if (!started)
                {
                    await call.AbandonAsync().ConfigureAwait(false);
                }
            }
        }

        private async Task<bool> AcceptedAsync(PhoneCall call, string callId, ILogger logger, CancellationToken stopping)
        {
            AcceptOutcome outcome = await control.AcceptAsync(
                callId,
                OpenAiLiveWire.AcceptBody(settings.Model, settings.Voice, LiveInstructions.Build(settings.Instructions, call.Brief)),
                stopping).ConfigureAwait(false);
            if (outcome == AcceptOutcome.Accepted)
            {
                return true;
            }

            OpenAiLiveLog.AcceptRefused(logger, callId, outcome);
            if (outcome == AcceptOutcome.Failed && !await control.RejectQuietlyAsync(callId, CallRefusal.Unavailable).ConfigureAwait(false))
            {
                OpenAiLiveLog.RejectFailed(logger, callId);
            }

            return false;
        }

        private async Task HangUpAsync(string callId, ILogger logger)
        {
            if (!await control.HangupQuietlyAsync(callId).ConfigureAwait(false))
            {
                OpenAiLiveLog.HangupFailed(logger, callId);
            }
        }

        private static string? Header(HttpContext http, string name) =>
            http.Request.Headers.TryGetValue(name, out StringValues value) ? value.ToString() : null;

        private static async Task<byte[]?> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
        {
            using MemoryStream body = new();
            byte[] buffer = new byte[8192];
            int read;
            while ((read = await request.Body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (body.Length + read > MaxBodyBytes)
                {
                    return null;
                }

                body.Write(buffer, 0, read);
            }

            return body.ToArray();
        }
    }
}
