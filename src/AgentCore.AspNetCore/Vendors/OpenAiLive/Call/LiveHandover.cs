namespace AgentCore.AspNetCore.Vendors.OpenAiLive.Call
{
    /// <summary>How a hand-over went.</summary>
    /// <param name="Outcome">Whether the line took the call, and whether the AI leg is closed already.</param>
    /// <param name="Receiving">The read loop's receive still open, or <see langword="null"/> once the sideband closed.</param>
    /// <param name="Why">Why the line did not take the call, for the log.</param>
    internal sealed record LiveHandover(LiveHandoverOutcome Outcome, Task<string?>? Receiving, string? Why = null);
}
