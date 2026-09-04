namespace AgentCore.Application.Runtime;

/// <summary>
/// The outermost tool call open on this flow of execution.
/// </summary>
internal static class OuterToolCall
{
    /// <summary>The id of the outermost tool call open on this flow, or <see langword="null"/> when none is.</summary>
    internal static string? Current => TurnAmbients.Current?.OuterCallId;

    /// <summary>Opens the scope of one tool call over this flow of execution.</summary>
    /// <param name="callId">The id of the tool call being opened.</param>
    /// <param name="nested">
    /// Whether a tool call was already open on this flow, so this one runs inside it. Reported from
    /// here rather than read separately by the caller, because the outermost call is what owns the
    /// ambient and a caller that read <see cref="Current"/> after this method would always see one.
    /// </param>
    /// <returns>The scope. Disposing it puts back what was ambient before.</returns>
    internal static IDisposable Enter(string callId, out bool nested)
    {
        ArgumentNullException.ThrowIfNull(callId);

        nested = Current is not null;

        return nested ? NoOpScope.Instance : TurnAmbients.Amend(ambients => ambients with { OuterCallId = callId });
    }

    /// <summary>A scope that puts nothing back, because it never changed anything.</summary>
    private sealed class NoOpScope : IDisposable
    {
        internal static readonly NoOpScope Instance = new();

        public void Dispose()
        {
        }
    }
}
