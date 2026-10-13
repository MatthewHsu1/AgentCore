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
        /// conflict renamed to the code every other transport answers. <paramref name="shownCalls"/>
        /// names the tool call ids the client's messages already show, or <see langword="null"/>
        /// outside a resume run: a resumed approval re-runs a call the client already shows, and
        /// announcing it again makes assistant-ui file the result under a second copy it then drops,
        /// so those announcements are dropped while their results still stream onto the asking turn's part.
        /// <paramref name="content"/> carries the content rules of this run: sources held until the
        /// words start, compaction as an in-place activity, the failure set, and the strip of an
        /// update's <c>AdditionalProperties</c> before the converter sees it.
        /// <paramref name="fileRefOf"/> mints the absolute file-door reference for one stored blob
        /// name, or <see langword="null"/> off the AG-UI route: the turn's published files leave as
        /// <c>agentcore_file</c> CUSTOM events at run finish, carrying <c>ref</c>, never a presigned URL.
        /// </summary>
        internal static async IAsyncEnumerable<BaseEvent> EventsAsync(
            ConversationSession conversation,
            IAsyncEnumerable<ChatResponseUpdate> updates,
            AgUiTurnIds.MessageIds ids,
            ChatRequestContext context,
            IReadOnlySet<string>? shownCalls,
            AgUiContent content,
            Func<string, string>? fileRefOf,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(conversation);
            ArgumentNullException.ThrowIfNull(updates);
            ArgumentNullException.ThrowIfNull(ids);
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(content);

            Exception? failure = null;
            List<BaseEvent> heldSources = [];
            TurnStreamFiles files = new();

            await foreach (BaseEvent evt in Split(updates, files, content, e => failure = e, cancellationToken)
                .AsAGUIEventStreamAsync(context, cancellationToken).ConfigureAwait(false))
            {
                evt.RawEvent = null;

                if (shownCalls is not null
                    && (evt is ToolCallStartEvent { ToolCallId: var started } && shownCalls.Contains(started)
                        || evt is ToolCallArgsEvent { ToolCallId: var argued } && shownCalls.Contains(argued)
                        || evt is ToolCallEndEvent { ToolCallId: var ended } && shownCalls.Contains(ended)))
                {
                    continue;
                }

                ids.Stamp(evt);

                // A source held back until the model's words start (or the run ends): a data part between
                // two tool calls splits assistant-ui's tool group, so live would draw two groups where a
                // reload draws one. The parts land after the turn's tool calls, the same order as history.
                if (evt is CustomEvent { Name: TurnStreamPart.Source })
                {
                    heldSources.Add(evt);
                    continue;
                }

                if (evt is TextMessageStartEvent or RunFinishedEvent or RunErrorEvent && heldSources.Count > 0)
                {
                    foreach (BaseEvent held in heldSources)
                    {
                        yield return held;
                    }

                    heldSources.Clear();
                }

                if (evt is RunFinishedEvent)
                {
                    if (fileRefOf is not null)
                    {
                        foreach (TurnStreamPart part in files.Resolve())
                        {
                            FilePayload payload = (FilePayload)part.Payload;
                            yield return new CustomEvent
                            {
                                Name = part.Member,
                                Value = JsonSerializer.SerializeToElement(payload with { Ref = fileRefOf(payload.Name) }, ResponsesJson.Options),
                            };
                        }
                    }

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
        /// run with RUN_ERROR. Sources and the commit leave as <c>CUSTOM</c> events, compaction as an
        /// in-place activity snapshot, and an update's <c>AdditionalProperties</c> are stripped first
        /// (see <see cref="AgUiContent.Prepare"/>): the fallback's disposition holds the model's thrown
        /// exception, which the converter cannot serialize.
        /// </summary>
        private static async IAsyncEnumerable<ChatResponseUpdate> Split(
            IAsyncEnumerable<ChatResponseUpdate> updates,
            TurnStreamFiles files,
            AgUiContent content,
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
                ChatResponseUpdate update = AgUiContent.Prepare(reader.Current);
                bool notice = update.Contents.Any(c => c is NoticeContent or TurnCommittedContent);
                if (!notice)
                {
                    files.Note(update);
                }
                foreach (TurnStreamPart part in TurnStreamParts.From(update, names))
                {
                    content.Note(part);

                    if (part.Member is TurnStreamPart.Compaction)
                    {
                        yield return new ChatResponseUpdate { RawRepresentation = content.ActivityFor(part) };
                    }
                    else if (part.Member is TurnStreamPart.Source or TurnStreamPart.MessageCommitted)
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
                if (rest.Count == update.Contents.Count)
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
