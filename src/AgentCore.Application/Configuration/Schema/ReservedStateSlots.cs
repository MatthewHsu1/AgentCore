namespace AgentCore.Application.Configuration.Schema;

/// <summary>
/// The three read-only slots that are always present.
/// </summary>
/// <remarks>
/// Section 8.3: a configuration may read them in a rule, and may not declare or write them.
/// </remarks>
public static class ReservedStateSlots
{
    /// <summary>The id of the stage the machine holds.</summary>
    public const string Stage = "stage";

    /// <summary>The zero-based index of the current turn.</summary>
    public const string TurnIndex = "turnIndex";

    /// <summary>The seconds the call has run.</summary>
    public const string CallDurationSeconds = "callDurationSeconds";

    /// <summary>Gets every reserved slot name.</summary>
    public static IReadOnlyList<string> All { get; } = [Stage, TurnIndex, CallDurationSeconds];

    /// <summary>Reports whether a name is reserved.</summary>
    /// <param name="name">The slot name to test.</param>
    /// <returns><see langword="true"/> when the name is reserved.</returns>
    public static bool Contains(string name)
        => string.Equals(name, Stage, StringComparison.Ordinal)
           || string.Equals(name, TurnIndex, StringComparison.Ordinal)
           || string.Equals(name, CallDurationSeconds, StringComparison.Ordinal);
}
