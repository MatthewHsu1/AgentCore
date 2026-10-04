using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Hooks.Gates;
using AgentCore.AspNetCore.Vendors.OpenAiLive.Webhook;

namespace AgentCore.AspNetCore.Vendors.OpenAiLive.Wire
{
    /// <summary>
    /// Every fact about the OpenAI GPT-Live SIP wire that no probe proved. The probes ran on the primary
    /// WebSocket, never on a SIP call. A real call must check each value here; correct them here
    /// and nowhere else.
    /// </summary>
    internal static class OpenAiLiveWire
    {
        /// <summary>The error event's type. Unverified: no probe saw one; the shape <c>{ error: { code, message } }</c> follows the Realtime API.</summary>
        internal const string ErrorEvent = "error";

        /// <summary>The code OpenAI answers a second accept or reject of one call with.</summary>
        internal const string DecisionAlreadyMade = "decision_already_made";

        internal static readonly Uri ApiBase = new("https://api.openai.com/");

        /// <summary>The incoming-call webhook's type. The GPT-Live docs say the first, the SIP guide and openai-agents the second.</summary>
        internal static readonly FrozenSet<string> IncomingCallEvents = FrozenSet.ToFrozenSet(["live.transport.incoming", "realtime.call.incoming"], StringComparer.Ordinal);

        // URLs as the GPT-Live docs give them.
        internal static string AcceptPath(string callId) => $"v1/live/sessions/{Uri.EscapeDataString(callId)}/accept";

        internal static string RejectPath(string callId) => $"v1/live/sessions/{Uri.EscapeDataString(callId)}/reject";

        internal static string HangupPath(string callId) => $"v1/live/sessions/{Uri.EscapeDataString(callId)}/hangup";

        internal static Uri AttachUri(Uri apiBase, string callId)
        {
            ArgumentNullException.ThrowIfNull(apiBase);
            UriBuilder attach = new(new Uri(apiBase, $"v1/live/sessions/{Uri.EscapeDataString(callId)}/attach"))
            {
                Scheme = apiBase.Scheme == Uri.UriSchemeHttp ? Uri.UriSchemeWs : Uri.UriSchemeWss,
            };
            return attach.Uri;
        }

        /// <summary>The SIP status a refusal is rejected with.</summary>
        internal static int SipStatusOf(CallRefusal refusal) => refusal switch
        {
            CallRefusal.Declined => 603,
            CallRefusal.Busy => 486,
            _ => 503,
        };

        /// <summary>
        /// The accept body: the proven <c>session.start</c> session shape from the probe logs, as the only field of the
        /// body. A real SIP call proved the wrapper: a flat body is refused with "session must be the only field".
        /// </summary>
        internal static JsonObject AcceptBody(string model, string voice, string instructions) => new()
        {
            ["session"] = new JsonObject
            {
                ["model"] = model,
                ["instructions"] = instructions,
                ["audio"] = new JsonObject { ["output"] = new JsonObject { ["voice"] = voice } },
                ["delegation"] = new JsonObject { ["type"] = "client" },
            },
        };

        /// <summary>
        /// The event that makes GPT-Live greet the caller instead of waiting for the caller's voice: instructions with no
        /// delegation, from OpenAI's live-conversations guide. A real SIP call without it stayed silent until the caller spoke.
        /// </summary>
        internal static JsonObject GreetFirst(string eventId) =>
            SessionFact(eventId, "Begin the conversation now: greet the caller as your instructions say, then pause and listen.");

        /// <summary>
        /// A fact for the whole session, under no delegation, from OpenAI's delegation guide: at most 500 tokens, such as
        /// <c>"The current date is December 10, 2024. Today is Tuesday."</c>
        /// </summary>
        internal static JsonObject SessionFact(string eventId, string content) => new()
        {
            ["type"] = "session.instructions.append",
            ["event_id"] = eventId,
            ["delegation_id"] = null,
            ["content"] = content,
        };

        internal static JsonObject RejectBody(CallRefusal refusal) => new() { ["status_code"] = SipStatusOf(refusal) };

        /// <summary>Reads an incoming-call webhook body: <c>{ type, data: { call_id | session_id, sip_headers: [ { name, value } ] } }</c>.</summary>
        /// <returns>The call, or <see langword="null"/> for any other event or a body that is not one.</returns>
        internal static LiveIncomingCall? ReadIncomingCall(byte[] body)
        {
            ArgumentNullException.ThrowIfNull(body);
            try
            {
                using JsonDocument document = JsonDocument.Parse(body);
                JsonElement root = document.RootElement;
                if (Text(root, "type") is not { } type || !IncomingCallEvents.Contains(type)
                    || !root.TryGetProperty("data", out JsonElement data)
                    || (Text(data, "call_id") ?? Text(data, "session_id")) is not { Length: > 0 } callId)
                {
                    return null;
                }

                Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
                if (data.TryGetProperty("sip_headers", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement header in list.EnumerateArray())
                    {
                        if (Text(header, "name") is { } name && Text(header, "value") is { } value)
                        {
                            headers[name] = headers.TryGetValue(name, out string? before) ? before + ", " + value : value;
                        }
                    }
                }

                return new LiveIncomingCall(callId, SipUser(headers.GetValueOrDefault("From")), SipUser(headers.GetValueOrDefault("To")), headers);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>The user part of a SIP or tel address: <c>"Name" &lt;sip:+15550100@host&gt;;tag=1</c> gives <c>+15550100</c>.</summary>
        internal static string? SipUser(string? header)
        {
            if (header is null)
            {
                return null;
            }

            int scheme = header.IndexOf("sip:", StringComparison.OrdinalIgnoreCase);
            if (scheme < 0)
            {
                scheme = header.IndexOf("tel:", StringComparison.OrdinalIgnoreCase);
            }

            if (scheme < 0)
            {
                return null;
            }

            int start = scheme + 4;
            int end = header.IndexOfAny(['@', ';', '>'], start);
            string user = end < 0 ? header[start..] : header[start..end];
            return user.Length == 0 ? null : user;
        }

        private static string? Text(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }
}
