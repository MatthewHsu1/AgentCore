using System.Text.Json.Nodes;

namespace AgentCore.AspNetCore.Vendors.OpenAiLive.Wire
{
    /// <summary>
    /// The sideband event names and shapes proven on the primary WebSocket. Whether the SIP
    /// sideband uses the same ones is unconfirmed; a fact no probe proved goes in <see cref="OpenAiLiveWire"/>.
    /// </summary>
    internal static class OpenAiLiveEvents
    {
        internal const string InputTranscriptDelta = "session.input_transcript.delta";

        internal const string OutputTranscriptDelta = "session.output_transcript.delta";

        internal const string DelegationCreated = "session.delegation.created";

        internal const string ThinkingAppend = "session.thinking.append";

        internal const string CommentaryAppend = "session.commentary.append";

        internal const string CommentaryAppended = "session.commentary.appended";

        internal const string Closed = "session.closed";

        /// <summary>Silent context for the open delegation.</summary>
        internal static JsonObject Thinking(string delegationId, string content, string eventId) => Append(ThinkingAppend, delegationId, content, eventId);

        /// <summary>Words GPT-Live speaks for the open delegation, lightly paraphrased. At most 500 tokens.</summary>
        internal static JsonObject Commentary(string delegationId, string content, string eventId) => Append(CommentaryAppend, delegationId, content, eventId);

        private static JsonObject Append(string type, string delegationId, string content, string eventId) => new()
        {
            ["type"] = type,
            ["delegation_id"] = delegationId,
            ["content"] = content,
            ["event_id"] = eventId,
        };
    }
}
