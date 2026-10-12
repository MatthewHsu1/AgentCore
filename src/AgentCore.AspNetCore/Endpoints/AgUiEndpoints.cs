using System.Text.Json;
using AGUI.Abstractions;
using AGUI.Formatting;
using AGUI.Server;
using AgentCore.Application.Conversation;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Runtime.Harness;
using AgentCore.Application.Runtime.Session;
using AgentCore.Application.Runtime.Turn;
using AgentCore.Application.Runtime.Turn.Lifecycle;
using AgentCore.AspNetCore.DependencyInjection;
using AgentCore.AspNetCore.DependencyInjection.Startup;
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

        /// <summary>Maps every entry on one route.</summary>
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

            return endpoints.MapPost(pattern, HandleAsync).WithMetadata(AgentCoreRouteMetadata.AgUi);
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

            if (!await AgentCoreConversationGate.AllowAsync(http, entry, input.ThreadId, resuming: false, cancellationToken).ConfigureAwait(false))
            {
                await AgUiRefusal.WriteAsync(http, input, 404, AgentCoreConversationGate.NotFoundCode, notFound).ConfigureAwait(false);
                return;
            }

            // AgentCore runs no client tools.
            input.Tools = [];
            ChatRequestContext context = input.ToChatRequestContext(Json);

            // The client resends the whole thread every run; the conversation already holds it, so only the newest words count.
            ChatMessage? words = AgUiInput.LastUserInput(context.Messages);
            if (words is null)
            {
                await AgUiRefusal.WriteAsync(http, input, 400, "no_user_message", "the run carries no user message with text, so there is no turn to run.").ConfigureAwait(false);
                return;
            }

            if (AgUiInput.CheckSize(words) is { } tooLarge)
            {
                await AgUiRefusal.WriteAsync(http, input, tooLarge.Status, tooLarge.Code, tooLarge.Message).ConfigureAwait(false);
                return;
            }

            ChatMessage turnInput = AgUiInput.Sanitize(words);

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

                ConversationTurnOrigin origin = await AgUiTurnIds.OriginAsync(
                    AgUiTurnIds.Read(input),
                    conversation.ConversationId,
                    http.RequestServices.GetRequiredService<IConversations>(),
                    cancellationToken).ConfigureAwait(false);

                // Started before the headers: only a started turn has opened its stored state and caught up on
                // other sessions', so only then does the stage name the one the turn speaks in.
                await using TurnRun run = await conversation.StartTurnAsync(turnInput, origin, cancellationToken).ConfigureAwait(false);
                AgUiTurnIds.MessageIds ids = new(run.TurnIndex);

                http.Response.StatusCode = StatusCodes.Status200OK;
                http.Response.ContentType = SseEventStreamFormatter.ServerSentEventsMediaType;
                http.Response.Headers.CacheControl = "no-cache";
                http.Response.Headers[ResponsesEndpointRouteBuilderExtensions.StageHeaderName] = conversation.Stage;

                await new SseEventStreamFormatter().WriteAsync(
                    AgUiEventStream.EventsAsync(conversation, run.Updates, ids, context, cancellationToken),
                    http.Response.Body,
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

        private static JsonSerializerOptions BuildJson()
        {
            JsonSerializerOptions options = new(AIJsonUtilities.DefaultOptions) { WriteIndented = false };
            options.TypeInfoResolverChain.Add(AGUIJsonSerializerContext.Default);
            options.MakeReadOnly();
            return options;
        }

        private static readonly JsonSerializerOptions Json = BuildJson();
    }
}
