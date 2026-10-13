using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Blobs;
using AgentCore.Application.Conversation;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime.Harness;
using AgentCore.Application.Transcript;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.AI;

namespace AgentCore.AspNetCore.Endpoints;

/// <summary>
/// <c>GET /agui/threads/{threadId}/turns</c>: the stored conversation as assistant-ui messages
/// (<c>ExportedMessageRepository.fromArray</c> input), one assistant message per turn, named <c>turn-{k}</c> as the
/// live stream names it. Parts keep row order (text, tool-call, text); a tool result is matched to its call by call
/// id across turns, because an approved call's result is written by the turn after the one that asked.
/// An approval splits the reply in two, as the live stream does: the asking turn stores the request (shown as the
/// tool call), and the resuming turn re-stores the same call, which is skipped there.
/// One read answers the newest <c>limit</c> turns before the <c>before</c> cursor; <c>nextCursor</c> asks for the next older page.
/// Behind the G-7 ownership hook. A host that declares two or more entries must supply a BeforeEntry hook that picks the entry, or these routes answer not found.
/// </summary>
internal static class AgUiTurnHistory
{
    /// <summary>How many turns a page holds when the caller names no limit.</summary>
    public const int DefaultTurns = 30;

    /// <summary>The most turns one page holds, whatever the caller asked for.</summary>
    public const int MaxTurns = 100;

    public static IEndpointConventionBuilder MapAgUiTurnHistory(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // Global like the file doors: the reference names no entry, and entry resolution runs per
        // request inside the ownership gate. No AgUi route metadata: the startup entry check only
        // walks tagged routes.
        return endpoints.MapGet("/agui/threads/{threadId}/turns", async (
            HttpContext http, string threadId, string? before, int? limit, IConversations conversations, CancellationToken ct) =>
        {
            if (!await AgUiFileRefs.OwnedAsync(http, threadId, ct).ConfigureAwait(false))
            {
                return Results.NotFound();
            }

            int? beforeTurn = null;
            if (before is not null)
            {
                if (!int.TryParse(before, NumberStyles.None, CultureInfo.InvariantCulture, out int turnIndex))
                {
                    return Results.Problem(
                        $"'{before}' is not a cursor. Send back the nextCursor a page answered with, or nothing at all.",
                        statusCode: StatusCodes.Status400BadRequest);
                }

                beforeTurn = turnIndex;
            }

            // One turn past each edge of the page: an approval's asking turn and its resuming turn may straddle it.
            int turns = Math.Clamp(limit ?? DefaultTurns, 1, MaxTurns);
            TranscriptWindow window = new(beforeTurn is { } edge ? Math.Min(edge, int.MaxValue - 1) + 1 : null, turns + 2);
            if (await conversations.LoadWindowAsync(threadId, window, ct).ConfigureAwait(false) is not { } stored)
            {
                return Results.NotFound();
            }

            HttpRequest request = http.Request;
            List<ConversationMessage> rows = [.. stored.Messages.OrderBy(r => r.Ordinal)];
            List<int> below = [.. rows.Select(r => r.TurnIndex).Distinct().Where(t => beforeTurn is not { } edge || t < edge).Order()];
            HashSet<int> page = [.. below.TakeLast(turns)];
            Dictionary<string, object?> results = rows
                .SelectMany(r => r.Content.Contents.OfType<FunctionResultContent>())
                .GroupBy(r => r.CallId)
                .ToDictionary(g => g.Key, g => g.Last().Result);

            Dictionary<string, string> pending = PendingByCallId(stored.Conversation.State);
            HashSet<string> asked = [];
            JsonArray messages = [];
            foreach (IGrouping<int, ConversationMessage> turn in rows.GroupBy(r => r.TurnIndex))
            {
                bool shown = page.Contains(turn.Key);
                JsonArray parts = [];
                string? userId = null;
                foreach (ConversationMessage row in turn)
                {
                    ChatMessage message = row.Content;
                    if (message.Role == ChatRole.User)
                    {
                        userId ??= row.MessageId;
                        JsonArray said = [.. message.Contents.OfType<TextContent>().Where(t => t.Text.Length > 0)
                            .Select(t => (JsonNode)new JsonObject { ["type"] = "text", ["text"] = t.Text })];
                        JsonArray attachments = Attachments(message, request, threadId);
                        if (shown && (said.Count > 0 || attachments.Count > 0))
                        {
                            messages.Add(new JsonObject
                            {
                                ["id"] = row.MessageId,
                                ["role"] = "user",
                                ["content"] = said,
                                ["attachments"] = attachments,
                            });
                        }

                        continue;
                    }

                    // A file the agent published is kept on the tool row of the call that published it.
                    foreach (FileContent made in message.Contents.OfType<FileContent>().Where(f => f.Kept))
                    {
                        parts.Add(new JsonObject
                        {
                            ["type"] = "file",
                            ["data"] = AgUiFileRefs.RefOf(request, threadId, made.Name),
                            ["mimeType"] = made.MediaType,
                            ["filename"] = made.Name,
                        });
                    }

                    if (message.Role != ChatRole.Assistant)
                    {
                        continue;
                    }

                    if (message.Text is { Length: > 0 } text)
                    {
                        parts.Add(new JsonObject { ["type"] = "text", ["text"] = text });
                    }

                    IEnumerable<FunctionCallContent> calls = message.Contents
                        .Select(c => c switch
                        {
                            ToolApprovalRequestContent { ToolCall: FunctionCallContent request } => asked.Add(request.CallId) ? request : null,
                            FunctionCallContent call when !asked.Contains(call.CallId) => call,
                            _ => null,
                        })
                        .OfType<FunctionCallContent>();
                    foreach (FunctionCallContent call in calls)
                    {
                        string args = JsonSerializer.Serialize(call.Arguments ?? new Dictionary<string, object?>(), AIJsonUtilities.DefaultOptions);
                        JsonObject part = new()
                        {
                            ["type"] = "tool-call",
                            ["toolCallId"] = call.CallId,
                            ["toolName"] = call.Name,
                            ["args"] = JsonNode.Parse(args),
                            ["argsText"] = args,
                        };
                        if (results.TryGetValue(call.CallId, out object? result))
                        {
                            part["result"] = result is string s ? s : JsonSerializer.SerializeToNode(result, AIJsonUtilities.DefaultOptions);
                        }
                        else if (pending.TryGetValue(call.CallId, out string? requestId))
                        {
                            part["approval"] = new JsonObject { ["id"] = requestId };
                        }

                        parts.Add(part);
                    }
                }

                if (!shown || parts.Count == 0)
                {
                    continue;
                }

                string replyId = turn.Last().MessageId;
                parts.Add(new JsonObject
                {
                    ["type"] = "data",
                    ["name"] = "agentcore_message_committed",
                    ["data"] = new JsonObject { ["user_message_id"] = userId, ["reply_message_id"] = replyId },
                });
                JsonObject reply = new()
                {
                    ["id"] = AgUiTurnIds.TurnMessageId(turn.Key),
                    ["role"] = "assistant",
                    ["content"] = parts,
                    ["status"] = new JsonObject { ["type"] = "complete", ["reason"] = "unknown" },
                };

                // A turn that ended asking for approval shows as react-ag-ui keeps a live interrupt: requires-action,
                // the open interrupts under metadata.custom.agui, and each gated call carrying approval.id.
                JsonArray interrupts = [.. parts
                    .Where(p => p!["approval"] is not null)
                    .Select(p => (JsonNode)new JsonObject
                    {
                        ["id"] = p!["approval"]!["id"]!.GetValue<string>(),
                        ["reason"] = "tool_call",
                        ["toolCallId"] = p["toolCallId"]!.GetValue<string>(),
                    })];
                if (interrupts.Count > 0)
                {
                    reply["status"] = new JsonObject { ["type"] = "requires-action", ["reason"] = "interrupt" };
                    reply["metadata"] = new JsonObject { ["custom"] = new JsonObject { ["agui"] = new JsonObject { ["interrupts"] = interrupts } } };
                }

                messages.Add(reply);
            }

            return Results.Text(
                new JsonObject { ["messages"] = messages, ["nextCursor"] = below.Count > turns ? page.Min().ToString(CultureInfo.InvariantCulture) : null }.ToJsonString(),
                "application/json");
        });
    }

    /// <summary>
    /// A stored user file as the assistant-ui attachment the composer made live. A filed upload carries its reference,
    /// never a link: the browser swaps it for a fresh link when it draws it, and sends it back as it is.
    /// </summary>
    private static JsonArray Attachments(ChatMessage message, HttpRequest request, string threadId)
    {
        JsonArray attachments = [];
        int index = 0;
        foreach (AIContent content in message.Contents)
        {
            (string? src, string? mediaType, string? name) = content switch
            {
                DataContent data => (data.Uri, data.MediaType, data.Name),
                UriContent uri => (uri.Uri.ToString(), uri.MediaType, null),
                FileContent file when InboundFiles.Is(file) => (AgUiFileRefs.RefOf(request, threadId, file.Name), file.MediaType, file.Title),
                _ => ((string?)null, (string?)null, (string?)null),
            };
            if (src is null)
            {
                continue;
            }

            bool image = mediaType!.StartsWith("image/", StringComparison.OrdinalIgnoreCase);
            JsonObject part = image
                ? new JsonObject { ["type"] = "image", ["image"] = src, ["filename"] = name }
                : new JsonObject { ["type"] = "file", ["data"] = src, ["mimeType"] = mediaType, ["filename"] = name };
            attachments.Add(new JsonObject
            {
                ["id"] = $"stored-{index++}",
                ["type"] = image ? "image" : "document",
                ["name"] = name ?? (image ? "image" : "file"),
                ["contentType"] = mediaType,
                ["status"] = new JsonObject { ["type"] = "complete" },
                ["content"] = new JsonArray(part),
            });
        }

        return attachments;
    }

    /// <summary>
    /// The approvals the conversation still waits on, by tool call id: MAF's pending queue
    /// (<c>_pendingApprovalRequests</c>), which the conversation files with its state after the asking turn.
    /// </summary>
    private static Dictionary<string, string> PendingByCallId(ConversationSessionState? state)
    {
        Dictionary<string, string> pending = new(StringComparer.Ordinal);
        if (state?.Providers.TryGetValue(PendingApprovalQueue.PendingStateKey, out JsonElement queue) != true || queue.ValueKind != JsonValueKind.Array)
        {
            return pending;
        }

        HashSet<string> held = state.Providers.TryGetValue(PendingApprovalQueue.HeldStateKey, out JsonElement answers) && answers.ValueKind == JsonValueKind.Array
            ? [.. answers.EnumerateArray().Select(a => a.TryGetProperty("requestId", out JsonElement id) ? id.GetString() ?? string.Empty : string.Empty)]
            : [];
        foreach (JsonElement entry in queue.EnumerateArray())
        {
            if (entry.TryGetProperty("requestId", out JsonElement id) && id.GetString() is { } requestId && !held.Contains(requestId)
                && entry.TryGetProperty("toolCall", out JsonElement call) && call.TryGetProperty("callId", out JsonElement callId)
                && callId.GetString() is { } toolCallId)
            {
                pending[toolCallId] = requestId;
            }
        }

        return pending;
    }
}
