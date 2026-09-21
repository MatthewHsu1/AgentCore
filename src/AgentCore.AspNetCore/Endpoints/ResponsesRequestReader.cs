using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Agents.AI.Hosting.OpenAI;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;

namespace AgentCore.AspNetCore.Endpoints
{
    /// <summary>What the Responses path reads off a request body, and how it answers a bad one.</summary>
    internal static class ResponsesRequestReader
    {
        /// <summary>The OpenAI error type every refused request reports.</summary>
        private const string InvalidRequestError = "invalid_request_error";

        /// <summary>Reads the request body as JSON.</summary>
        /// <returns>The body, or <see langword="null"/> once a 400 has been answered for a body that is not JSON.</returns>
        internal static async Task<JsonElement?> ReadBodyAsync(HttpContext http, CancellationToken cancellationToken)
        {
            try
            {
                return await JsonSerializer
                    .DeserializeAsync<JsonElement>(http.Request.Body, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (JsonException exception)
            {
                await WriteErrorAsync(
                    http,
                    StatusCodes.Status400BadRequest,
                    "the request body is not well-formed JSON: " + exception.Message,
                    "malformed_body",
                    cancellationToken).ConfigureAwait(false);
                return null;
            }
        }

        /// <summary>
        /// Reads what the body asks to run, through the protocol parse. <paramref name="approving"/>
        /// says the body carries an approval answer, which the parse alone refuses.
        /// </summary>
        /// <returns>The run, or <see langword="null"/> once a 400 has been answered for a body the parse refused.</returns>
        /// <remarks>
        /// The protocol treats a conversation id and a response id as mutually exclusive, and the
        /// helper prefers the response chain; the refused-parse fallback reads the same precedence off
        /// the body.
        /// </remarks>
        internal static async Task<ResponsesRunInput?> ReadRunInputAsync(
            HttpContext http,
            JsonElement body,
            bool approving,
            CancellationToken cancellationToken)
        {
            try
            {
                OpenAIResponsesRunRequest runRequest = OpenAIResponses.ToAgentRunRequest(body);

                return new ResponsesRunInput(
                    NullIfBlank(runRequest.ConversationId),
                    [.. runRequest.Messages],
                    NullIfBlank(OpenAIResponses.GetSessionStoreId(runRequest)));
            }
            catch (ArgumentException exception)
            {
                // An approval answer carries no words, only the request it answers — and the
                // protocol parse refuses a turn with no input. The continuation still names the
                // conversation, so read it off the body and run the answer alone.
                if (!approving)
                {
                    await WriteErrorAsync(
                        http,
                        StatusCodes.Status400BadRequest,
                        "the request is not a Responses request: " + exception.Message,
                        "invalid_body",
                        cancellationToken).ConfigureAwait(false);
                    return null;
                }

                return new ResponsesRunInput(
                    NullIfBlank(ReadConversationId(body)),
                    [],
                    NullIfBlank(ReadContinuationId(body)));
            }
        }

        private static string? NullIfBlank(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        /// <summary>Reads the last user message that carries words, the session owning the rest.</summary>
        internal static ChatMessage? LastUserMessage(IReadOnlyList<ChatMessage> messages)
        {
            ChatMessage? picked = null;
            foreach (ChatMessage message in messages)
            {
                if (message.Role == ChatRole.User && message.Text is { Length: > 0 })
                {
                    picked = message;
                }
            }

            return picked;
        }

        /// <summary>Reads whether any user message of the run carries words.</summary>
        internal static string? LastUserText(IReadOnlyList<ChatMessage> messages)
        {
            return LastUserMessage(messages)?.Text;
        }

        /// <summary>Reads the conversation id off a body the protocol parse refused.</summary>
        internal static string? ReadConversationId(JsonElement body)
        {
            if (body.TryGetProperty("conversation", out JsonElement conversation))
            {
                if (conversation.ValueKind == JsonValueKind.String)
                {
                    return conversation.GetString();
                }

                if (conversation.ValueKind == JsonValueKind.Object
                    && conversation.TryGetProperty("id", out JsonElement id)
                    && id.ValueKind == JsonValueKind.String)
                {
                    return id.GetString();
                }
            }

            return null;
        }

        /// <summary>Reads the continuation off a body the protocol parse refused.</summary>
        internal static string? ReadContinuationId(JsonElement body)
        {
            return body.TryGetProperty("previous_response_id", out JsonElement response)
                && response.ValueKind == JsonValueKind.String
                && response.GetString() is { Length: > 0 } responseId
                ? responseId
                : ReadConversationId(body);
        }

        /// <summary>Answers one failure in the shape an OpenAI client reads.</summary>
        internal static async Task WriteErrorAsync(
            HttpContext http,
            int status,
            string message,
            string code,
            CancellationToken cancellationToken)
        {
            http.Response.StatusCode = status;

            await http.Response.WriteAsJsonAsync(
                new JsonObject
                {
                    ["error"] = new JsonObject
                    {
                        ["message"] = message,
                        ["type"] = InvalidRequestError,
                        ["code"] = code,
                        ["param"] = null,
                    },
                },
                cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Builds the id of one conversation.</summary>
        /// <returns>The id, in the shape an OpenAI client already reads.</returns>
        internal static string NewConversationId()
        {
            return string.Create(CultureInfo.InvariantCulture, $"conv_{Guid.NewGuid():N}");
        }
    }
}
