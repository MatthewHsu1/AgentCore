using System.Buffers;
using System.Net.ServerSentEvents;
using System.Text.Json;
using System.Text.Json.Nodes;
using AGUI.Abstractions;
using Microsoft.Extensions.AI;

namespace AgentCore.AspNetCore.Endpoints
{
    /// <summary>
    /// The content rules of the AG-UI endpoint, for one run.
    /// <list type="bullet">
    /// <item>A compaction notice is one <c>ACTIVITY_SNAPSHOT</c> per pass (<c>activityType: "compaction"</c>,
    /// one <c>messageId</c> per turn, <c>replace: true</c>), so the end replaces the start in place.</item>
    /// <item>A failed tool answer leaves as <c>TOOL_CALL_RESULT</c> with <c>isError: true</c> and
    /// <c>_meta.agentcore.failed</c>: the only failure flag react-ag-ui reads off the stream (its MCP
    /// tool-result path). AGUI.Abstractions has no such field, so the SSE writer adds it.</item>
    /// <item>An update's <c>AdditionalProperties</c> never reach the converter: the fallback reply carries
    /// a <c>TurnDisposition</c> holding the model's exception, and serializing it into <c>rawEvent</c>
    /// ended the run with <c>RUN_ERROR</c> before the fallback text left.</item>
    /// </list>
    /// </summary>
    internal sealed class AgUiContent(int turnIndex)
    {
        private readonly HashSet<string> _failed = new(StringComparer.Ordinal);

        /// <summary>Gets the activity id one compaction pass of this turn replaces in place.</summary>
        public string CompactionMessageId => AgUiTurnIds.TurnMessageId(turnIndex) + ".compaction";

        /// <summary>Remembers the calls whose answer AgentCore marked as a failure.</summary>
        /// <param name="part">One browser part of a streaming update.</param>
        public void Note(TurnStreamPart part)
        {
            if (part.Payload is ToolPayload { Phase: "result", Failed: true } tool)
            {
                _ = _failed.Add(tool.CallId);
            }
        }

        /// <summary>Reads whether the answer of one call was marked as a failure.</summary>
        /// <param name="callId">The id the call and its answer share.</param>
        public bool Failed(string callId) => _failed.Contains(callId);

        /// <summary>Frames one compaction notice as the activity snapshot that replaces the pass's note in place.</summary>
        /// <param name="part">The compaction part, carrying a <see cref="CompactionPayload"/>.</param>
        public ActivitySnapshotEvent ActivityFor(TurnStreamPart part)
        {
            return new ActivitySnapshotEvent
            {
                MessageId = CompactionMessageId,
                ActivityType = "compaction",
                Content = JsonSerializer.SerializeToElement(part.Payload, part.Payload.GetType(), ResponsesJson.Options),
                Replace = true,
            };
        }

        /// <summary>
        /// Strips an update's <c>AdditionalProperties</c> before AGUI.Server sees it: the converter
        /// serializes every update into <c>rawEvent</c> first, and a value it cannot serialize
        /// (the fallback's disposition holds the model's thrown exception) ends the run with
        /// <c>RUN_ERROR</c>. This is the one strip; the yield below needs no second check.
        /// </summary>
        /// <param name="update">One update of the turn stream.</param>
        public static ChatResponseUpdate Prepare(ChatResponseUpdate update)
        {
            if (update.AdditionalProperties is not { Count: > 0 })
            {
                return update;
            }

            return new ChatResponseUpdate(update.Role, update.Contents)
            {
                MessageId = update.MessageId,
                ResponseId = update.ResponseId,
                ConversationId = update.ConversationId,
                AuthorName = update.AuthorName,
                CreatedAt = update.CreatedAt,
                FinishReason = update.FinishReason,
                ModelId = update.ModelId,
                RawRepresentation = update.RawRepresentation,
            };
        }

        /// <summary>Writes the events as SSE, the way <c>SseEventStreamFormatter</c> does, plus the failure flag.</summary>
        /// <param name="events">The run's AG-UI events.</param>
        /// <param name="output">The response body.</param>
        /// <param name="content">The content rules of this run, naming the failed calls.</param>
        /// <param name="cancellationToken">A token to cancel the write.</param>
        public static Task WriteAsync(
            IAsyncEnumerable<BaseEvent> events,
            Stream output,
            AgUiContent content,
            CancellationToken cancellationToken)
        {
            return SseFormatter.WriteAsync(
                Items(events, cancellationToken),
                output,
                (item, writer) => Serialize(item.Data, writer, content),
                cancellationToken);
        }

        private static async IAsyncEnumerable<SseItem<BaseEvent>> Items(
            IAsyncEnumerable<BaseEvent> events,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (BaseEvent evt in events.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                yield return new SseItem<BaseEvent>(evt);
            }
        }

        private static void Serialize(BaseEvent evt, IBufferWriter<byte> buffer, AgUiContent content)
        {
            using Utf8JsonWriter writer = new(buffer);
            if (evt is ToolCallResultEvent result && content.Failed(result.ToolCallId))
            {
                JsonObject node = JsonSerializer.SerializeToNode(evt, AGUIJsonSerializerContext.Default.BaseEvent)!.AsObject();
                node["isError"] = true;
                node["_meta"] = new JsonObject { ["agentcore"] = new JsonObject { ["failed"] = true } };
                node.WriteTo(writer);
                return;
            }

            JsonSerializer.Serialize(writer, evt, AGUIJsonSerializerContext.Default.BaseEvent);
        }
    }
}
