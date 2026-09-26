using System.Text.Json.Nodes;

namespace AgentCore.AspNetCore.Tests.Endpoints
{
    /// <summary>Reads what one Responses answer carries.</summary>
    internal static class ResponsesAnswer
    {
        /// <summary>Reads the assistant text of one answer.</summary>
        /// <param name="body">The answer.</param>
        /// <returns>Every output text, joined.</returns>
        internal static string OutputText(this JsonNode body)
        {
            return string.Join("", body["output"]?.AsArray()
                        .SelectMany(o => o?["content"]?.AsArray() ?? [])
                        .Select(c => c?["text"]?.GetValue<string>() ?? "") ?? []);
        }

        /// <summary>Reads the id a later turn hangs off: the conversation, or the response.</summary>
        /// <param name="body">The answer.</param>
        /// <returns>The continuation id.</returns>
        internal static string ContinuationId(this JsonNode body)
        {
            return body["conversation"] is JsonObject conversation
                && conversation["id"]?.GetValue<string>() is { Length: > 0 } conversationId
                ? conversationId
                : body["id"]?.GetValue<string>()
                ?? throw new InvalidOperationException("Answer carries no continuation id.");
        }
    }
}
