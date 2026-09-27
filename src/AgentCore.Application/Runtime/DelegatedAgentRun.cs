using System.Text.Json;
using AgentCore.Application.Diagnostics;
using AgentCore.Application.Runtime.Harness;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentCore.Application.Runtime
{
#pragma warning disable MAAI001 // BackgroundAgentsProvider is evaluation-only in Microsoft.Agents.AI 1.21.0.
    internal static class DelegatedAgentRun
    {
        /// <summary>Runs one inner agent for one delegating tool call.</summary>
        /// <param name="inner">The agent being delegated to.</param>
        /// <param name="query">What the outer agent asked, in words.</param>
        /// <param name="nested">The parent turn, stripped per K42 — or null outside a turn.</param>
        /// <param name="tools">The tools this delegation is offered, or null for none.</param>
        /// <param name="backgroundProviders">
        /// Every background provider the document compiled. The delegation's session is fresh and this
        /// call is its only owner, so its background children start releasing off this call's own path
        /// when it returns — on fault or cancel too, the same duty a conversation's own end discharges for
        /// its session. Awaiting the release inline here, under <see cref="CancellationToken.None"/>, would
        /// hold the outer turn (and a barge-in on it) for up to
        /// <see cref="BackgroundSessionRelease"/>'s timeout whenever this delegation started a child that
        /// ignores its cancel (R4-5).
        /// </param>
        /// <param name="cancellationToken">Cancels the run.</param>
        /// <returns>What the inner agent answered, as the outer agent reads it.</returns>
        internal static async ValueTask<object?> RunAsync(
            AIAgent inner,
            string query,
            TurnInvocation? nested,
            IReadOnlyList<AITool>? tools,
            IReadOnlyList<BackgroundAgentsProvider> backgroundProviders,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(inner);
            ArgumentNullException.ThrowIfNull(query);
            ArgumentNullException.ThrowIfNull(backgroundProviders);

            AgentSession session = await inner.CreateSessionAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                ChatOptions chat = new()
                {
                    AdditionalProperties = [],
                };

                if (nested is not null)
                {
                    chat.AdditionalProperties[TurnInvocation.ArgumentsKey] = nested;
                }

                if (tools is { Count: > 0 })
                {
                    chat.Tools = [.. tools];
                }

                AgentResponse response = await inner.RunAsync(
                    [new ChatMessage(ChatRole.User, query)],
                    session,
                    new ChatClientAgentRunOptions(chat),
                    cancellationToken).ConfigureAwait(false);

                return response.Text is { } text
                    ? JsonSerializer.SerializeToElement(text)
                    : null;
            }
            finally
            {
                if (backgroundProviders.Count > 0)
                {
                    ReleaseInBackground(
                        backgroundProviders,
                        session,
                        nested?.ConversationId ?? "(delegation outside a turn)",
                        nested?.Logger ?? NullLogger.Instance);
                }
            }
        }

        /// <summary>
        /// Starts releasing the delegation's session off the caller's path, and logs the outcome. Nothing
        /// here awaits the release, so it can never stall the outer turn or a barge-in on it.
        /// </summary>
        private static void ReleaseInBackground(
            IReadOnlyList<BackgroundAgentsProvider> backgroundProviders, AgentSession session, string conversationId, ILogger logger)
        {
            _ = Task.Run(() => ReleaseLoggingFailureAsync(backgroundProviders, session, conversationId, logger));
        }

        private static async Task ReleaseLoggingFailureAsync(
            IReadOnlyList<BackgroundAgentsProvider> backgroundProviders, AgentSession session, string conversationId, ILogger logger)
        {
            try
            {
                await BackgroundSessionRelease.ReleaseAsync(
                    backgroundProviders, session, conversationId, logger, CancellationToken.None).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Nothing awaits this release once the delegation returns, so nothing may escape it.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                SessionOwnerLog.BackgroundReleaseFailed(logger, conversationId, exception);
            }
        }
    }
#pragma warning restore MAAI001
}
