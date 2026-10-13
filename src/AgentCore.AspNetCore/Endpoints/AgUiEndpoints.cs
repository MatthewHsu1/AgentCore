using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentCore.Application.Conversation;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Runtime.Harness;
using AgentCore.Application.Runtime.Session;
using AgentCore.Application.Runtime.Turn;
using AgentCore.Application.Runtime.Turn.Lifecycle;
using AgentCore.Application.Transcript;
using AgentCore.AspNetCore.DependencyInjection;
using AgentCore.AspNetCore.DependencyInjection.Startup;
using AGUI.Abstractions;
using AGUI.Formatting;
using AGUI.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.AspNetCore.Endpoints
{
    /// <summary>
    /// The AG-UI path: an AG-UI-compatible <c>POST /agui/{entry}</c> beside the Responses route.
    /// A run clears the refusal prefix, then runs one turn of the entry's conversation and streams
    /// the reply back as AG-UI events.
    /// </summary>
    public static class AgUiEndpointRouteBuilderExtensions
    {
        /// <summary>The route every entry answers on when the host names none.</summary>
        public const string DefaultPattern = "/agui/{entry}";

        /// <summary>Maps every entry on <see cref="DefaultPattern"/>, with the URL naming the entry.</summary>
        /// <param name="endpoints">The route builder of the host.</param>
        /// <returns>The mapped endpoint, so a host adds its own conventions.</returns>
        public static IEndpointConventionBuilder MapAgUi(this IEndpointRouteBuilder endpoints)
        {
            return endpoints.MapAgUi(DefaultPattern);
        }

        /// <summary>Maps every entry on one route, with the file doors beside it, all behind the G-7 ownership hook.</summary>
        /// <param name="endpoints">The route builder of the host.</param>
        /// <param name="pattern">
        /// The route to answer on. It carries the <c>{entry}</c> parameter, or a hook's
        /// <see cref="AgentCore.Application.Hooks.AgentHook.BeforeEntryAsync"/> picks the entry. Startup fails when it has neither.
        /// </param>
        /// <returns>The mapped endpoint, so a host adds its own conventions.</returns>
        public static IEndpointConventionBuilder MapAgUi(this IEndpointRouteBuilder endpoints, string pattern)
        {
            ArgumentNullException.ThrowIfNull(endpoints);
            ArgumentException.ThrowIfNullOrEmpty(pattern);

            IEndpointConventionBuilder run = endpoints.MapPost(pattern, HandleAsync).WithMetadata(AgentCoreRouteMetadata.AgUi);

            // The thread routes name no entry, so a second MapAgUi on the same builder would map them twice and make every request ambiguous.
            if (ThreadRoutesMapped.TryAdd(endpoints, new object()))
            {
                _ = endpoints.MapAgUiFileLink();
                _ = endpoints.MapAgUiFileDoor();
                _ = endpoints.MapAgUiTurnHistory();
            }

            return run;
        }

        /// <summary>Runs one AG-UI run: the refusal prefix, then one turn of the entry's conversation.</summary>
        private static async Task HandleAsync(HttpContext http)
        {
            CancellationToken cancellationToken = http.RequestAborted;
            RunAgentInput? input;
            try
            {
                input = await JsonSerializer.DeserializeAsync(http.Request.Body, AGUIJsonSerializerContext.Default.RunAgentInput, cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException e)
            {
                await AgUiRefusal.WriteAsync(http, null, 400, "malformed_body", e.Message).ConfigureAwait(false);
                return;
            }

            if (input is null || string.IsNullOrWhiteSpace(input.ThreadId))
            {
                await AgUiRefusal.WriteAsync(http, input, 400, "missing_thread", "a run names the thread it belongs to.").ConfigureAwait(false);
                return;
            }

            if (await AgentCoreEntries.ResolveAsync(http, cancellationToken).ConfigureAwait(false) is not { } entry)
            {
                await AgUiRefusal.WriteAsync(http, input, 403, "entry_refused", "this route runs no entry for this caller.").ConfigureAwait(false);
                return;
            }

            EntryRegistry entries = http.RequestServices.GetRequiredService<AgentCoreBoot>().Entries;
            if (!entries.Agents.TryGetValue(entry, out AgentCoreAgent? agent))
            {
                await AgUiRefusal.WriteAsync(http, input, 404, "unknown_entry", EntryRegistry.UnknownEntryMessage(entry, entries.Entries)).ConfigureAwait(false);
                return;
            }

            string notFound = $"no thread '{input.ThreadId}' for this caller. Create the thread first.";
            if (await http.RequestServices.GetRequiredService<IConversations>().GetAsync(input.ThreadId, cancellationToken).ConfigureAwait(false) is null)
            {
                await AgUiRefusal.WriteAsync(http, input, 404, AgentCoreConversationGate.NotFoundCode, notFound).ConfigureAwait(false);
                return;
            }

            if (!await AgentCoreConversationGate.AllowAsync(http, entry, input.ThreadId, resuming: (input.Resume?.Count ?? 0) > 0, cancellationToken).ConfigureAwait(false))
            {
                await AgUiRefusal.WriteAsync(http, input, 404, AgentCoreConversationGate.NotFoundCode, notFound).ConfigureAwait(false);
                return;
            }

            // AgentCore runs no client tools.
            input.Tools = [];
            ChatRequestContext context = input.ToChatRequestContext(Json);
            IList<AGUIResume> resume = input.Resume ?? [];

            // The client resends the whole thread every run; the conversation already holds it, so only the newest words count.
            // Beside a resume, only words sent after the interrupted message count (assistant-ui's steerAway).
            ChatMessage? words = resume.Count == 0
                ? AgUiAttachments.LastUserInput(context.Messages)
                : AgUiApprovals.WordsBeside(input, context.Messages);

            if (resume.Count == 0 && words is null)
            {
                await AgUiRefusal.WriteAsync(http, input, 400, "no_user_message", "the run carries no user message with text or a file, so there is no turn to run.").ConfigureAwait(false);
                return;
            }

            if (words is not null && resume.Count == 0)
            {
                (ChatMessage Resolved, (int Status, string Code, string Message)? Refused) resolved =
                    await AgUiFileRefs.ResolveAsync(
                        words,
                        input.ThreadId,
                        http.Request.PathBase,
                        http.RequestServices.GetRequiredService<IConversations>(),
                        cancellationToken).ConfigureAwait(false);
                if (resolved.Refused is { } refused)
                {
                    await AgUiRefusal.WriteAsync(http, input, refused.Status, refused.Code, refused.Message).ConfigureAwait(false);
                    return;
                }

                words = resolved.Resolved;
            }
            // A regenerate names the stored user row it re-asks; the row's files stand in for the
            // body's bytes, so a live regenerate files nothing twice and an after-reload one (whose
            // references ResolveAsync already restored) keeps working. A count mismatch files normally.
            // Raw URLs never stand in: after resolve the only UriContent left is non-reference links,
            // which the attachment check below must still refuse.
            if (words is not null && resume.Count == 0
                && words.Contents.Any(c => c is DataContent)
                && AgUiTurnIds.Read(input).MessageId is { } named)
            {
                words = await StandInStoredFilesAsync(
                    words,
                    named,
                    input.ThreadId,
                    http.RequestServices.GetRequiredService<IConversations>(),
                    cancellationToken).ConfigureAwait(false);
            }

            if (words is not null && AgUiAttachments.Check(words, new AgUiAttachmentLimits()) is { } denied)
            {
                await AgUiRefusal.WriteAsync(http, input, denied.Status, denied.Code, denied.Message).ConfigureAwait(false);
                return;
            }

            if (words is not null && AgUiInput.CheckSize(words) is { } tooLarge)
            {
                await AgUiRefusal.WriteAsync(http, input, tooLarge.Status, tooLarge.Code, tooLarge.Message).ConfigureAwait(false);
                return;
            }

            ChatMessage? sanitized = words is null ? null : AgUiInput.Sanitize(words);

            Microsoft.Agents.AI.AgentSession session;
            try
            {
                session = await agent.CreateSessionAsync(input.ThreadId, cancellationToken).ConfigureAwait(false);
            }
            catch (ConversationInUseException e)
            {
                await AgUiRefusal.WriteAsync(http, input, 409, "conversation_in_use", e.Message).ConfigureAwait(false);
                return;
            }

            ConversationSession conversation;
            try
            {
                conversation = await agent.EnterRequestAsync(session, cancellationToken).ConfigureAwait(false);
            }
            catch (ConversationTurnConflictException)
            {
                await AgUiRefusal.WriteAsync(http, input, 409, ResponsesTurnConflict.Code, ResponsesTurnConflict.BusyMessage).ConfigureAwait(false);
                return;
            }

            try
            {
                if (CallerTimeZone.Parse(http.Request.Headers[ResponsesEndpointRouteBuilderExtensions.TimeZoneHeaderName]) is { } zone)
                {
                    CallerTimeZone.Set(session, zone);
                }

                ChatMessage? turnInput = sanitized;
                ConversationTurnOrigin? origin = null;

                if (resume.Count > 0)
                {
                    AgUiApprovals.Reading reading = await AgUiApprovals.ReadAsync(
                        conversation,
                        resume,
                        sanitized,
                        http.RequestServices.GetRequiredService<IConversations>(),
                        cancellationToken).ConfigureAwait(false);

                    if (reading.Replay)
                    {
                        await AgUiApprovals.WriteReplayAsync(http, input).ConfigureAwait(false);
                        return;
                    }

                    if (reading.Message is null)
                    {
                        await AgUiRefusal.WriteAsync(http, input, 409, reading.Code!, reading.Error!).ConfigureAwait(false);
                        return;
                    }
                    turnInput = reading.Message;
                }

                // The caller's files go to the blob store; the row keeps a reference. Skipped when no store.
                if (turnInput is not null && http.RequestServices.GetService<IBlobStore>() is { } blobs)
                {
                    turnInput = await InboundFiles.FileAsync(blobs, conversation.ConversationId, turnInput, cancellationToken).ConfigureAwait(false);
                }

                // A resume run carries no anchor and the endpoint ignores one: only words name where the turn hangs.
                if (sanitized is not null)
                {
                    origin = await AgUiTurnIds.OriginAsync(
                        AgUiTurnIds.Read(input),
                        conversation.ConversationId,
                        http.RequestServices.GetRequiredService<IConversations>(),
                        cancellationToken).ConfigureAwait(false);
                }

                // Started before the headers: only a started turn has opened its stored state and caught up on
                // other sessions', so only then does the stage name the one the turn speaks in.
                // Without a resume the null-words run was refused above; with one the reading returned or set it.
                await using TurnRun run = await conversation.StartTurnAsync(turnInput!, origin, cancellationToken).ConfigureAwait(false);
                AgUiTurnIds.MessageIds ids = new(run.TurnIndex);

                // A resumed approval re-runs calls the client's messages already show; their announcements
                // are dropped so each result lands on the asking turn's part.
                HashSet<string>? shown = resume.Count == 0
                    ? null
                    : [.. context.Messages.SelectMany(m => m.Contents.OfType<FunctionCallContent>()).Select(c => c.CallId)];

                http.Response.StatusCode = StatusCodes.Status200OK;
                http.Response.ContentType = SseEventStreamFormatter.ServerSentEventsMediaType;
                http.Response.Headers.CacheControl = "no-cache";
                http.Response.Headers[ResponsesEndpointRouteBuilderExtensions.StageHeaderName] = conversation.Stage;

                AgUiContent content = new(run.TurnIndex);

                await AgUiContent.WriteAsync(
                    AgUiEventStream.EventsAsync(conversation, run.Updates, ids, context, shown, content, name => AgUiFileRefs.RefOf(http.Request, conversation.ConversationId, name), cancellationToken),
                    http.Response.Body,
                    content,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (ConversationTurnConflictException) when (!http.Response.HasStarted)
            {
                await AgUiRefusal.WriteAsync(http, input, 409, ResponsesTurnConflict.Code, ResponsesTurnConflict.ConflictMessage).ConfigureAwait(false);
            }
            catch (InvalidOperationException e) when (!http.Response.HasStarted)
            {
                await AgUiRefusal.WriteAsync(http, input, 409, "turn_refused", e.Message).ConfigureAwait(false);
            }
            finally
            {
                await conversation.Busy.ExitRequestAsync().ConfigureAwait(false);
            }
        }
        /// <summary>
        /// Puts the stored user row's files back in place of the body's bytes, when <paramref name="named"/>
        /// names a stored user row of this conversation and the body carries the same count of media parts.
        /// Anything else (a new message id, an edit that swaps files) keeps the body's bytes for the normal
        /// file path. Text still comes from the body; only the file parts stand in.
        /// </summary>
        private static async ValueTask<ChatMessage> StandInStoredFilesAsync(
            ChatMessage words, string named, string threadId, IConversations conversations, CancellationToken cancellationToken)
        {
            StoredConversation? stored = await conversations.LoadWindowAsync(threadId, new TranscriptWindow(null, int.MaxValue), cancellationToken).ConfigureAwait(false);
            if (stored?.Messages.FirstOrDefault(r => r.MessageId == named && r.Content.Role == ChatRole.User) is not { } row)
            {
                return words;
            }

            List<FileContent> files = [.. row.Content.Contents.OfType<FileContent>()];
            if (files.Count == 0 || files.Count != words.Contents.Count(c => c is DataContent))
            {
                return words;
            }

            int file = 0;
            List<AIContent> contents = [];
            foreach (AIContent content in words.Contents)
            {
                contents.Add(content is DataContent ? files[file++] : content);
            }

            return new ChatMessage(words.Role, contents)
            {
                AuthorName = words.AuthorName,
                MessageId = words.MessageId,
                CreatedAt = words.CreatedAt,
                AdditionalProperties = words.AdditionalProperties,
                RawRepresentation = words.RawRepresentation,
            };
        }


        private static JsonSerializerOptions BuildJson()
        {
            JsonSerializerOptions options = new(AIJsonUtilities.DefaultOptions) { WriteIndented = false };
            options.TypeInfoResolverChain.Add(AGUIJsonSerializerContext.Default);
            options.MakeReadOnly();
            return options;
        }

        private static readonly JsonSerializerOptions Json = BuildJson();

        private static readonly ConditionalWeakTable<IEndpointRouteBuilder, object> ThreadRoutesMapped = new();
    }
}
