using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentCore.Application.Runtime.Turn;
using AgentCore.Application.Transcript;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Runtime.Agents.Graph
{
    /// <summary>
    /// One agent of a graph row, carrying the turn that runs the graph into each of its runs.
    /// </summary>
    internal sealed class GraphParticipantAgent(AIAgent inner) : DelegatingAIAgent(inner)
    {
        private static readonly ConditionalWeakTable<AgentSession, AgentSession> Graphs = [];

        /// <inheritdoc />
        protected override async ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
        {
            AgentSession? graph = CurrentRunContext?.Session;
            AgentSession session = await InnerAgent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);

            return Tied(session, graph);
        }

        /// <inheritdoc />
        protected override async ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement serializedState,
            JsonSerializerOptions? jsonSerializerOptions = null,
            CancellationToken cancellationToken = default)
        {
            AgentSession? graph = CurrentRunContext?.Session;
            AgentSession session = await InnerAgent
                .DeserializeSessionAsync(serializedState, jsonSerializerOptions, cancellationToken)
                .ConfigureAwait(false);

            return Tied(session, graph);
        }

        /// <inheritdoc />
        protected override async Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            (AgentRunOptions? filed, TurnInvocation? turn) = Filed(session, options);
            try
            {
                return await InnerAgent.RunAsync(messages, session, filed, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                // A file the store already kept is the caller's, even when the run fails after publishing it.
                Deliver(turn, Name ?? Id);
            }
        }

        /// <inheritdoc />
        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            (AgentRunOptions? filed, TurnInvocation? turn) = Filed(session, options);

            await foreach (AgentResponseUpdate update in InnerAgent
                .RunStreamingAsync(messages, session, filed, cancellationToken)
                .ConfigureAwait(false))
            {
                yield return update;
                Deliver(turn, Name ?? Id);
            }

            Deliver(turn, Name ?? Id);
        }

        /// <summary>Sends every file the run published so far to the turn's caller, around the workflow.</summary>
        /// <param name="turn">The participant's copy of the turn, or <see langword="null"/> when no turn runs the graph.</param>
        /// <param name="author">The participant's name.</param>
        private static void Deliver(TurnInvocation? turn, string? author)
        {
            if (turn is { Files: { } files, Notices: { } notices })
            {
                foreach (FileContent file in files.TakeAll())
                {
                    files.Deliver(file);
                    notices.Post(new FileNotice(file, author));
                }
            }
        }

        private static AgentSession Tied(AgentSession session, AgentSession? graph)
        {
            if (graph is not null)
            {
                Graphs.AddOrUpdate(session, graph);
            }

            return session;
        }

        /// <summary>Files the graph's current turn for one participant run, and returns the options that carry it.</summary>
        /// <returns>
        /// The options: the turn's own when the caller passed none, a copy of the caller's chat options with the turn
        /// on, or the caller's unchanged when no turn runs the graph or they are not chat options. And the participant's
        /// copy of the turn, or <see langword="null"/> when no turn runs the graph.
        /// </returns>
        private static (AgentRunOptions? Options, TurnInvocation? Turn) Filed(AgentSession? session, AgentRunOptions? options)
        {
            if (session is null
                || !Graphs.TryGetValue(session, out AgentSession? graph)
                || TurnRegistry.For(graph) is not { } turn)
            {
                return (options, null);
            }

            TurnInvocation participant = turn with { Clarifications = null, Nested = true, Sources = null, Files = new TurnFiles(turn.Files) };
            TurnRegistry.Set(session, participant);

            // A copy every run: a workflow executor hands the same options object to each of its runs, and
            // the framework writes into the options it is given.
            if (options?.Clone() is not ChatClientAgentRunOptions carried)
            {
                return (options is null ? participant.RunOptions() : options, participant);
            }

            carried.ChatOptions ??= new ChatOptions();
            carried.ChatOptions.AdditionalProperties ??= [];
            carried.ChatOptions.AdditionalProperties[TurnInvocation.ArgumentsKey] = participant;
            return (carried, participant);
        }
    }
}
