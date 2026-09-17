using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;

namespace AgentCore.AspNetCore.Endpoints;

/// <summary>What the Responses path reads off a request body, and how it answers a bad one.</summary>
internal static class ResponsesRequestReader
{
    /// <summary>Reads the last user message that carries words, the session owning the rest.</summary>
    internal static ChatMessage? LastUserMessage(IReadOnlyList<ChatMessage> messages)
    {
        ChatMessage? picked = null;
        foreach (var message in messages)
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
        => LastUserMessage(messages)?.Text;

    /// <summary>Reads the conversation id off a body the protocol parse refused.</summary>
    internal static string? ReadConversationId(JsonElement body)
    {
        if (body.TryGetProperty("conversation", out var conversation))
        {
            if (conversation.ValueKind == JsonValueKind.String)
            {
                return conversation.GetString();
            }

            if (conversation.ValueKind == JsonValueKind.Object
                && conversation.TryGetProperty("id", out var id)
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
        if (body.TryGetProperty("previous_response_id", out var response)
            && response.ValueKind == JsonValueKind.String
            && response.GetString() is { Length: > 0 } responseId)
        {
            return responseId;
        }

        return ReadConversationId(body);
    }

    /// <summary>Answers one failure in the shape an OpenAI client reads.</summary>
    internal static async Task WriteErrorAsync(
        HttpContext http,
        int status,
        string message,
        string type,
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
                    ["type"] = type,
                    ["code"] = code,
                    ["param"] = null,
                },
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Builds the id of one conversation.</summary>
    /// <returns>The id, in the shape an OpenAI client already reads.</returns>
    internal static string NewConversationId()
        => string.Create(CultureInfo.InvariantCulture, $"conv_{Guid.NewGuid():N}");
}
