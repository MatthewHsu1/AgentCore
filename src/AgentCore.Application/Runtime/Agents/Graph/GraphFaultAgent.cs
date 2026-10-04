using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Runtime.Agents.Graph
{
    /// <summary>
    /// Rethrows the fault a graph node raised, and the caller's cancel. <c>AsAIAgent()</c> reports a fault as an
    /// <see cref="ErrorContent"/> update whose <see cref="AgentResponseUpdate.RawRepresentation"/> is the
    /// <see cref="ExecutorFailedEvent"/> or <see cref="WorkflowErrorEvent"/>, and never throws; without this the
    /// turn reads an empty reply.
    /// </summary>
    internal sealed class GraphFaultAgent(AIAgent inner, bool drain) : DelegatingAIAgent(inner)
    {
        /// <inheritdoc />
        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            // The merged response drops each update's RawRepresentation, so the non-streaming path reads the stream.
            return RunCoreStreamingAsync(messages, session, options, cancellationToken).ToAgentResponseAsync(cancellationToken);
        }

        /// <inheritdoc />
        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Exception? fault = null;

            await foreach (AgentResponseUpdate update in base.RunCoreStreamingAsync(messages, session, options, cancellationToken)
                .ConfigureAwait(false))
            {
                if (fault is not null)
                {
                    continue;
                }

                if (FaultOf(update) is { } thisFault)
                {
                    fault = thisFault;

                    if (!drain)
                    {
                        break;
                    }

                    continue;
                }

                yield return update;
            }

            if (fault is not null)
            {
                ExceptionDispatchInfo.Capture(fault).Throw();
            }

            cancellationToken.ThrowIfCancellationRequested();
        }

        private static Exception? FaultOf(AgentResponseUpdate update)
        {
            Exception? fault = update.RawRepresentation switch
            {
                ExecutorFailedEvent failed => failed.Data
                    ?? new InvalidOperationException($"The graph node '{failed.ExecutorId}' failed, and MAF reported no exception."),
                WorkflowErrorEvent error => error.Exception
                    ?? new InvalidOperationException("The graph reported a workflow error with no exception."),
                _ => null,
            };

            while (fault is TargetInvocationException { InnerException: { } inner })
            {
                fault = inner;
            }

            return fault;
        }
    }
}
