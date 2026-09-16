using Microsoft.Agents.AI;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Runtime;

// Runs one delegated agent directly — the same RunAsync every other run goes through —
// instead of the framework's agent-as-function machinery, which builds the nested run's
// options where no caller can file the turn. The bridge files the nested turn and the
// tools this delegation is offered on the nested run's own options, exactly like the
// loop does for the outer run. One nested run, one fresh session: concurrent delegations
// never share one.
internal static class DelegatedAgentRun
{
    /// <summary>Runs one inner agent for one delegating tool call.</summary>
    /// <param name="inner">The agent being delegated to.</param>
    /// <param name="query">What the outer agent asked, in words.</param>
    /// <param name="nested">The parent turn, stripped per K42 — or null outside a turn.</param>
    /// <param name="tools">The tools this delegation is offered, or null for none.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>What the inner agent answered, as the outer agent reads it.</returns>
    internal static async ValueTask<object?> RunAsync(
        AIAgent inner,
        string query,
        TurnInvocation? nested,
        IReadOnlyList<AITool>? tools,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(query);

        var session = await inner.CreateSessionAsync(cancellationToken).ConfigureAwait(false);

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

        var response = await inner.RunAsync(
            [new ChatMessage(ChatRole.User, query)],
            session,
            new ChatClientAgentRunOptions(chat),
            cancellationToken).ConfigureAwait(false);

        // Marshaled like the framework's own agent-as-function result: the outer loop reads
        // words either way, but callers pin the JsonElement shape.
        return response.Text is { } text
            ? JsonSerializer.SerializeToElement(text)
            : null;
    }
}
