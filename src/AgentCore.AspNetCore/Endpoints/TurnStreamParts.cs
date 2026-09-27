using System.Text.Json.Nodes;
using AgentCore.Application.Runtime.Compaction;
using AgentCore.Application.Runtime.Turn;
using AgentCore.Application.Tools;
using AgentCore.Application.Transcript;
using AgentCore.Domain.Sources;
using Microsoft.Extensions.AI;

namespace AgentCore.AspNetCore.Endpoints
{
    /// <summary>
    /// The non-text parts of one streaming update, read once for every text endpoint.
    /// </summary>
    /// <remarks>
    /// A citation, a tool half, an approval ask, and a notice ride <c>ChatResponseUpdate</c>
    /// contents that no OpenAI shape carries. Each endpoint frames them its own way, but what they
    /// mean — and what the browser reads — is one mapping, kept here so a new content kind lands in
    /// one place: one <c>case</c> below, one payload record. Text stays endpoint-side: completions
    /// deltas it, Responses leaves it to the framework converter.
    /// </remarks>
    internal static class TurnStreamParts
    {
        /// <summary>Reads one update's browser parts, in wire order: sources, tools, approvals, notices.</summary>
        /// <param name="update">One update of the stream.</param>
        /// <param name="toolNames">The pairing of call id to tool name for this turn, written and read here.</param>
        /// <returns>One part for each browser payload the update carries, possibly none.</returns>
        internal static IEnumerable<TurnStreamPart> From(ChatResponseUpdate update, ToolCallNames toolNames)
        {
            foreach (SourceContent cited in update.Contents.OfType<SourceContent>())
            {
                yield return new TurnStreamPart(TurnStreamPart.Source, SourcePayloadOf(cited));
            }

            foreach (ToolPayload tool in ToolPayloadsOf(update, toolNames))
            {
                yield return new TurnStreamPart(TurnStreamPart.Tool, tool);
            }

            foreach (ToolApprovalRequestContent asked in update.Contents.OfType<ToolApprovalRequestContent>())
            {
                // A request arrives with no text, so without this the update falls into the
                // empty-text skip below and the caller never learns a tool waits on them.
                if (asked.ToolCall is FunctionCallContent call)
                {
                    yield return new TurnStreamPart(TurnStreamPart.Approval, new ApprovalPayload
                    {
                        RequestId = asked.RequestId,
                        Tool = call.Name,
                        Arguments = ArgumentsOf(call),
                    });
                }
            }

            foreach (NoticeContent notice in update.Contents.OfType<NoticeContent>())
            {
                if (NoticePartOf(notice) is { } part)
                {
                    yield return part;
                }
            }

            foreach (TurnCommittedContent committed in update.Contents.OfType<TurnCommittedContent>())
            {
                yield return new TurnStreamPart(TurnStreamPart.MessageCommitted, new MessageCommittedPayload
                {
                    UserMessageId = committed.UserMessageId,
                    ReplyMessageId = committed.ReplyMessageId,
                });
            }
        }

        /// <summary>Frames one notice for the browser, or <see langword="null"/> for a kind it does not show.</summary>
        private static TurnStreamPart? NoticePartOf(NoticeContent notice)
        {
            return notice switch
            {
                CompactionContent compaction
                    => new TurnStreamPart(TurnStreamPart.Compaction, new CompactionPayload { Phase = compaction.Phase, Outcome = compaction.Outcome }),
                _ => null,
            };
        }

        /// <summary>Reads the tool facts one update carries, in the order they appear on it.</summary>
        private static IEnumerable<ToolPayload> ToolPayloadsOf(
            ChatResponseUpdate update,
            ToolCallNames toolNames)
        {
            foreach (AIContent content in update.Contents)
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
                        JsonNode? answer = ToolResultJson.ToNode(result.Result);
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
        {
            return new()
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
        }

        /// <summary>Reads what the model passed to one tool, or <see langword="null"/> when it passed nothing.</summary>
        private static JsonObject? ArgumentsOf(FunctionCallContent call)
        {
            return call.Arguments is { Count: > 0 } arguments ? ToolArgumentJson.ToJsonObject(arguments) : null;
        }

        /// <summary>Reads what one tool answered, as the browser receives it.</summary>
        private static JsonNode? ResultOf(FunctionResultContent result, JsonNode? answer)
        {
            return result.Exception is { } failure
                        ? JsonValue.Create(failure.GetType().Name + ": " + failure.Message)
                        : answer;
        }
    }

    /// <summary>One browser payload of a streaming update: the dialect member it goes out under, and its value.</summary>
    /// <param name="Member">The <c>agentcore_*</c> member name, one of the constants on this record.</param>
    /// <param name="Payload">The value serialized under <paramref name="Member"/>, by its runtime type.</param>
    internal sealed record TurnStreamPart(string Member, object Payload)
    {
        /// <summary>Where one answer came from: a <see cref="SourcePayload"/>.</summary>
        public const string Source = "agentcore_source";

        /// <summary>One half of one tool call: a <see cref="ToolPayload"/>.</summary>
        public const string Tool = "agentcore_tool";

        /// <summary>One tool call waiting on the caller: an <see cref="ApprovalPayload"/>.</summary>
        public const string Approval = "agentcore_approval";

        /// <summary>One compaction notice: a <see cref="CompactionPayload"/>.</summary>
        public const string Compaction = "agentcore_compaction";

        /// <summary>One file the turn produced: a <see cref="FilePayload"/>.</summary>
        public const string File = "agentcore_file";

        /// <summary>The ids one turn's commit wrote: a <see cref="MessageCommittedPayload"/>.</summary>
        public const string MessageCommitted = "agentcore_message_committed";
    }
}
