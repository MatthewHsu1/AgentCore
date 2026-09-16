using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using Microsoft.Agents.AI.Workflows;

namespace AgentCore.Application.Configuration.Compilation;

internal static class GraphGuardGate
{
    /// <summary>The reason a gate reports when the run carries no state.</summary>
    internal const string NoStateMessage =
        "A guarded graph edge asked for the state of the running call, and the run carries none. "
        + "Row 4 of the section 8.2 compile table reads the state of one call, and the graph-state "
        + "wrapper files it from the turn before the run starts. Run the graph through a CallSession, "
        + "which files the turn on the run's session. This throws and does not answer false, because "
        + "a guarded edge that quietly became unconditional is the silent graph failure section 8.2 "
        + "refuses to ship.";

    /// <summary>Forwards one edge message past a guarded edge, or holds it when the guard is false.</summary>
    /// <param name="message">The message that reached the edge, forwarded unchanged.</param>
    /// <param name="context">The run the edge belongs to, carrying the filed snapshot.</param>
    /// <param name="guard">The edge's <c>when:</c> guard.</param>
    /// <param name="evaluator">The shared evaluator that runs the rule.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <exception cref="InvalidOperationException">The run carries no state.</exception>
    internal static async ValueTask RouteAsync(
        object? message,
        IWorkflowContext context,
        GuardReference guard,
        IGuardEvaluator evaluator,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(evaluator);

        var snapshot = await context
            .ReadStateAsync<IReadOnlyDictionary<string, JsonNode?>>(
                GraphStateEntry.SlotsKey, GraphStateEntry.StateScope, cancellationToken)
            .ConfigureAwait(false);

        if (snapshot is null)
        {
            throw new InvalidOperationException(NoStateMessage);
        }

        if (evaluator.Evaluate(guard, snapshot))
        {
            await context.SendMessageAsync(message!, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }
}
