using System.Text.Json;
using System.Text.Json.Nodes;
using AGUI.Abstractions;
using AGUI.Server;
using AgentCore.Application.Conversation;
using AgentCore.Application.Runtime.Session;
using AgentCore.Application.Runtime.Turn;
using AgentCore.Application.Tools;
using AgentCore.Application.Transcript;
using Microsoft.Extensions.AI;

namespace AgentCore.AspNetCore.Endpoints
{
    /// <summary>
    /// The AG-UI events of one turn: AgentCore's own contents leave as <c>CUSTOM</c> events
    /// beside AGUI.Server's converted stream, every event ships without its raw twin, and the
    /// run opens and closes on a snapshot of the stage it speaks in.
    /// </summary>
    internal static class AgUiEventStream
    {
        /// <summary>
        /// Converts one turn's reply into AG-UI events: the split stream through AGUI.Server,
        /// stamped with this run's ids, framed by stage snapshots, with a mid-stream turn
        /// conflict renamed to the code every other transport answers.
        /// </summary>
        internal static async IAsyncEnumerable<BaseEvent> EventsAsync(
            ConversationSession conversation,
            IAsyncEnumerable<ChatResponseUpdate> updates,
            AgUiTurnIds.MessageIds ids,
            ChatRequestContext context,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(conversation);
            ArgumentNullException.ThrowIfNull(updates);
            ArgumentNullException.ThrowIfNull(ids);
            ArgumentNullException.ThrowIfNull(context);

            Exception? failure = null;

            await foreach (BaseEvent evt in Split(updates, e => failure = e, cancellationToken)
                .AsAGUIEventStreamAsync(context, cancellationToken).ConfigureAwait(false))
            {
                evt.RawEvent = null;
                ids.Stamp(evt);

                if (evt is RunFinishedEvent)
                {
                    yield return Stage(conversation, after: true);
                }

                if (evt is RunErrorEvent error && failure is ConversationTurnConflictException)
                {
                    error.Code = ResponsesTurnConflict.Code;
                    error.Message = ResponsesTurnConflict.ConflictMessage;
                }

                yield return evt;

                if (evt is RunStartedEvent)
                {
                    yield return Stage(conversation, after: false);
                }
            }
        }

        /// <summary>
        /// Takes AgentCore's own contents off each update before AGUI.Server sees them: the converter
        /// serializes every update into rawEvent first, and a content type it does not know ends the
        /// run with RUN_ERROR. Stripped contents leave as <c>CUSTOM</c> events instead.
        /// </summary>
        private static async IAsyncEnumerable<ChatResponseUpdate> Split(
            IAsyncEnumerable<ChatResponseUpdate> updates,
            Action<Exception> onFailure,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            ToolCallNames names = new();
            await using IAsyncEnumerator<ChatResponseUpdate> reader = updates.GetAsyncEnumerator(cancellationToken);
            while (true)
            {
                try
                {
                    if (!await reader.MoveNextAsync().ConfigureAwait(false))
                    {
                        yield break;
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    onFailure(e);
                    throw;
                }

                ChatResponseUpdate update = reader.Current;
                bool notice = update.Contents.Any(c => c is NoticeContent or TurnCommittedContent);

                foreach (TurnStreamPart part in TurnStreamParts.From(update, names))
                {
                    if (part.Member is TurnStreamPart.Source or TurnStreamPart.Compaction or TurnStreamPart.MessageCommitted)
                    {
                        yield return new ChatResponseUpdate
                        {
                            RawRepresentation = new CustomEvent
                            {
                                Name = part.Member,
                                Value = JsonSerializer.SerializeToElement(part.Payload, part.Payload.GetType(), ResponsesJson.Options),
                            },
                        };
                    }
                }

                if (notice)
                {
                    continue;
                }

                List<AIContent> rest = [.. update.Contents.Where(c => c is not (SourceContent or FileContent))];
                if (rest.Count == update.Contents.Count && update.AdditionalProperties is null)
                {
                    yield return update;
                }
                else if (rest.Count > 0)
                {
                    yield return new ChatResponseUpdate(update.Role, rest)
                    {
                        MessageId = update.MessageId,
                        ResponseId = update.ResponseId,
                        AuthorName = update.AuthorName,
                        CreatedAt = update.CreatedAt,
                        FinishReason = update.FinishReason,
                        ModelId = update.ModelId,
                    };
                }
            }
        }

        private static StateSnapshotEvent Stage(ConversationSession conversation, bool after)
        {
            JsonObject state = new()
            {
                ["stage"] = after ? conversation.LastTurn?.StageAfter ?? conversation.Stage : conversation.Stage,
                ["phase"] = after ? "after" : "before",
            };
            if (after)
            {
                state["isTerminal"] = conversation.LastTurn?.IsTerminal ?? conversation.IsComplete;
            }

            return new StateSnapshotEvent { Snapshot = JsonSerializer.SerializeToElement(state) };
        }
    }
}
