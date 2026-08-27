namespace AgentCore.Application.Runtime;

/// <summary>
/// The outermost tool call open on this flow of execution.
/// </summary>
internal static class OuterToolCall
{
    /// <summary>The id of the outermost tool call open on this flow, or <see langword="null"/> when none is.</summary>
    internal static string? Current => TurnAmbients.Current?.OuterCallId;

    /// <summary>Opens the scope of one tool call over this flow of execution.</summary>
    internal static IDisposable Enter(string callId)
    {
        ArgumentNullException.ThrowIfNull(callId);

        return Current is not null ? NoOpScope.Instance : TurnAmbients.Amend(ambients => ambients with { OuterCallId = callId });
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
