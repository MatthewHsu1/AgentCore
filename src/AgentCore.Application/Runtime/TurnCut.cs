namespace AgentCore.Application.Runtime
{
    /// <summary>What the user saw or heard of a reply before it was cut.</summary>
    /// <param name="ShownText">
    /// The text that reached the user, or <see langword="null"/> for everything the engine yielded.
    /// </param>
    /// <param name="Played">How much of the reply played, when a voice layer knows it.</param>
    public sealed record TurnCut(string? ShownText, TimeSpan? Played);
}
