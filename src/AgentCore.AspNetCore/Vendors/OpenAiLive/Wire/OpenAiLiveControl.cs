using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using AgentCore.Application.Hooks.Gates;
using AgentCore.AspNetCore.Vendors.OpenAiLive.Webhook;

namespace AgentCore.AspNetCore.Vendors.OpenAiLive.Wire
{
    /// <summary>OpenAI's call-control requests for a SIP call: accept, reject, hang up, refer. URLs and bodies come from <see cref="OpenAiLiveWire"/>.</summary>
    internal sealed class OpenAiLiveControl(HttpClient http, Task<OpenAiLiveCredentials> credentials)
    {
        /// <summary>The deadline of a best-effort request that must not wait on the host's token: a hang-up at shutdown, a reject after a failed accept.</summary>
        internal static readonly TimeSpan QuietDeadline = TimeSpan.FromSeconds(5);

        internal async Task<AcceptOutcome> AcceptAsync(string callId, JsonObject body, CancellationToken cancellationToken)
        {
            try
            {
                using HttpResponseMessage response = await PostAsync(OpenAiLiveWire.AcceptPath(callId), body, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return AcceptOutcome.Accepted;
                }

                string answer = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return answer.Contains(OpenAiLiveWire.DecisionAlreadyMade, StringComparison.Ordinal) ? AcceptOutcome.AlreadyDecided : AcceptOutcome.Failed;
            }
            catch (HttpRequestException)
            {
                return AcceptOutcome.Failed;
            }
        }

        internal Task<bool> RejectAsync(string callId, CallRefusal refusal, CancellationToken cancellationToken) =>
            SucceedsAsync(OpenAiLiveWire.RejectPath(callId), OpenAiLiveWire.RejectBody(refusal), cancellationToken);

        internal Task<bool> HangupAsync(string callId, CancellationToken cancellationToken) =>
            SucceedsAsync(OpenAiLiveWire.HangupPath(callId), [], cancellationToken);

        // A 2xx says only that OpenAI relayed the REFER, not that the line took the call (probe T1).
        internal Task<bool> ReferAsync(string callId, Uri target, CancellationToken cancellationToken) =>
            SucceedsAsync(OpenAiLiveWire.ReferPath(callId), OpenAiLiveWire.ReferBody(target), cancellationToken);

        /// <summary>Hangs up on its own <see cref="QuietDeadline"/>; a deadline or a cancel gives <see langword="false"/>, never an exception.</summary>
        internal Task<bool> HangupQuietlyAsync(string callId) => QuietlyAsync(token => HangupAsync(callId, token));

        /// <summary>Rejects on its own <see cref="QuietDeadline"/>; a deadline or a cancel gives <see langword="false"/>, never an exception.</summary>
        internal Task<bool> RejectQuietlyAsync(string callId, CallRefusal refusal) => QuietlyAsync(token => RejectAsync(callId, refusal, token));

        private static async Task<bool> QuietlyAsync(Func<CancellationToken, Task<bool>> send)
        {
            using CancellationTokenSource limit = new(QuietDeadline);
            try
            {
                return await send(limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        private async Task<bool> SucceedsAsync(string path, JsonObject body, CancellationToken cancellationToken)
        {
            try
            {
                using HttpResponseMessage response = await PostAsync(path, body, cancellationToken).ConfigureAwait(false);
                return response.IsSuccessStatusCode;
            }
            catch (HttpRequestException)
            {
                return false;
            }
        }

        private async Task<HttpResponseMessage> PostAsync(string path, JsonObject body, CancellationToken cancellationToken)
        {
            OpenAiLiveCredentials keys = await credentials.ConfigureAwait(false);
            using HttpRequestMessage request = new(HttpMethod.Post, path)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", keys.ApiKey);
            return await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }
}
