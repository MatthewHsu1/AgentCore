using System.Text.Json.Nodes;
using AgentCore.Application.Tools;
using AgentCore.Application.Transcript;
using AgentCore.Domain.Sources;
using Microsoft.Extensions.AI;

namespace AgentCore.AspNetCore.Endpoints;

/// <summary>
/// The non-text parts of one streaming update, read once for every text endpoint.
/// </summary>
/// <remarks>
/// A drawing, a citation, a tool half, and an approval ask ride <c>ChatResponseUpdate</c>
/// contents that no OpenAI shape carries. Each endpoint frames them its own way, but what
/// they mean — and what the browser reads — is one mapping, kept here so a new content
/// kind lands in one place. Text stays endpoint-side: completions deltas it, Responses
/// leaves it to the framework converter.
/// </remarks>
internal static class TurnStreamParts
{
    /// <summary>Reads one update's browser parts, in wire order: renders, sources, tools, approvals.</summary>
    /// <param name="update">One update of the stream.</param>
    /// <param name="toolNames">The pairing of call id to tool name for this turn, written and read here.</param>
    /// <returns>One part for each browser payload the update carries, possibly none.</returns>
    internal static IEnumerable<TurnStreamPart> From(ChatResponseUpdate update, ToolCallNames toolNames)
    {
        foreach (var drawn in update.Contents.OfType<RenderContent>())
        {
            yield return new TurnStreamRender(new RenderedPayload { Name = drawn.Name, Data = drawn.Data });
        }

        foreach (var cited in update.Contents.OfType<SourceContent>())
        {
            yield return new TurnStreamSource(SourcePayloadOf(cited));
        }

        foreach (var tool in ToolPayloadsOf(update, toolNames))
        {
            yield return new TurnStreamTool(tool);
        }

        foreach (var asked in update.Contents.OfType<ToolApprovalRequestContent>())
        {
            // A request arrives with no text, so without this the update falls into the
            // empty-text skip below and the caller never learns a tool waits on them.
            if (asked.ToolCall is FunctionCallContent call)
            {
                yield return new TurnStreamApproval(new ApprovalPayload
                {
                    RequestId = asked.RequestId,
                    Tool = call.Name,
                    Arguments = ArgumentsOf(call),
                });
            }
        }
    }

    /// <summary>Reads the tool facts one update carries, in the order they appear on it.</summary>
    private static IEnumerable<ToolPayload> ToolPayloadsOf(
        ChatResponseUpdate update,
        ToolCallNames toolNames)
    {
        foreach (var content in update.Contents)
        {
            switch (content)
            {
                case FunctionCallContent call:
                    toolNames.Called(call);
                    yield return new ToolPayload
                    {
                        CallId = call.CallId,
                        Name = call.Name,
                        Phase = "call",
                        Arguments = ArgumentsOf(call),
                    };
                    break;

                case FunctionResultContent result:
                    // The answer has no declared shape, so it goes through the one reader that knows
                    // every shape it arrives in. Reading it twice would let the two disagree.
                    var answer = ToolResultJson.ToNode(result.Result);
                    yield return new ToolPayload
                    {
                        CallId = result.CallId,
                        // A result that arrives with no call before it should not be possible, and
                        // naming it after its id beats naming it nothing if it ever is.
                        Name = toolNames.Of(result) ?? result.CallId,
                        Phase = "result",
                        Result = ResultOf(result, answer),
                        Failed = result.Exception is not null || ToolErrorResult.IsError(answer),
                    };
                    break;

                default:
                    break;
            }
        }
    }

    /// <summary>Reads one cited source into what the browser receives.</summary>
    private static SourcePayload SourcePayloadOf(SourceContent cited)
        => new()
        {
            CallId = cited.CallId,
            Id = cited.Source.SourceId,
            SourceType = cited.Source.Kind == SourceKind.Url ? "url" : "document",
            Title = cited.Source.Title,
            Locator = cited.Source.Locator,
            Url = cited.Source.Url,
            MediaType = cited.Source.MediaType,
            Origin = cited.Source.Origin,
        };

    /// <summary>Reads what the model passed to one tool, or <see langword="null"/> when it passed nothing.</summary>
    private static JsonObject? ArgumentsOf(FunctionCallContent call)
        => call.Arguments is { Count: > 0 } arguments ? ToolArgumentJson.ToJsonObject(arguments) : null;

    /// <summary>Reads what one tool answered, as the browser receives it.</summary>
    private static JsonNode? ResultOf(FunctionResultContent result, JsonNode? answer)
        => result.Exception is { } failure
            ? JsonValue.Create(failure.GetType().Name + ": " + failure.Message)
            : answer;
}

/// <summary>One browser payload of a streaming update.</summary>
internal abstract record TurnStreamPart;

/// <summary>One thing a chunk asks the browser to draw.</summary>
internal sealed record TurnStreamRender(RenderedPayload Payload) : TurnStreamPart;

/// <summary>Where one answer came from, as the browser reads it.</summary>
internal sealed record TurnStreamSource(SourcePayload Payload) : TurnStreamPart;

/// <summary>One half of one tool call, as the browser reads it.</summary>
internal sealed record TurnStreamTool(ToolPayload Payload) : TurnStreamPart;

/// <summary>One tool call waiting on the caller, as the browser reads it.</summary>
internal sealed record TurnStreamApproval(ApprovalPayload Payload) : TurnStreamPart;
