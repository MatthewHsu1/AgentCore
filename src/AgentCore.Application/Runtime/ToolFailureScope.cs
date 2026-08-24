using AgentCore.Domain.Audit;

namespace AgentCore.Application.Runtime;

/// <summary>One tool call that did not run to completion, as the function-invocation loop saw it.</summary>
internal sealed record ToolFailure
{
    /// <summary>Gets the name the MODEL called.</summary>

    public required string ToolName { get; init; }

    /// <summary>Gets the id the model gave this one call.</summary>
    public required string ToolCallId { get; init; }

    /// <summary>Gets which of the two ways a tool call fails this one was.</summary>
    public required ToolFailureKind Kind { get; init; }

    /// <summary>Gets what went wrong, in one sentence. It never holds a secret value.</summary>
    public required string Message { get; init; }
}

/// <summary>
/// Where a tool failure is reported on this flow of execution.
/// </summary>
internal static class ToolFailureScope
{
    /// <summary>Opens the scope of one turn over this flow of execution.</summary>
    /// <param name="listener">What to do with each failure. It is called on the framework's tool flow.</param>
    /// <returns>The scope. Disposing it puts back the listener that was ambient before.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="listener"/> is <see langword="null"/>.</exception>
    internal static IDisposable Enter(Action<ToolFailure> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);

        return TurnAmbients.Amend(ambients => ambients with { OnToolFailure = listener });
    }

    /// <summary>Reports one tool failure to the call running on this flow of execution.</summary>
    /// <param name="failure">What did not run to completion.</param>
    internal static void Report(ToolFailure failure) => TurnAmbients.Current?.OnToolFailure?.Invoke(failure);
}
