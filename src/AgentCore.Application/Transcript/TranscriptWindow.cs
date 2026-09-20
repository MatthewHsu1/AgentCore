namespace AgentCore.Application.Transcript;

/// <summary>
/// One page of a conversation's words, counted in turns rather than rows. A turn holds a question,
/// the tool calls that answered it, and the reply, and a page boundary never falls inside one, so a
/// reader can draw a page without a reply whose tool calls are on the page before it.
/// </summary>
/// <param name="BeforeTurn">
/// Only turns with a smaller index are read, or <see langword="null"/> to read back from the newest.
/// </param>
/// <param name="Turns">How many turns at most. Positive.</param>
/// <exception cref="ArgumentOutOfRangeException"><paramref name="Turns"/> is not positive.</exception>
public readonly record struct TranscriptWindow(int? BeforeTurn, int Turns)
{
    /// <summary>How many turns at most. Positive, except on <c>default</c>, which reads an empty page.</summary>
    public int Turns { get; init; } = Turns > 0
        ? Turns
        : throw new ArgumentOutOfRangeException(nameof(Turns), Turns, "A window holds at least one turn.");

    /// <summary>Whether a turn falls before the window's start.</summary>
    /// <param name="turnIndex">The turn to test.</param>
    /// <returns><see langword="true"/> when the turn is one the window may hold.</returns>
    public bool Admits(int turnIndex) => BeforeTurn is not { } before || turnIndex < before;
}
