namespace AgentCore.Application.Hooks.Notices
{
    /// <summary>The caller spoke over the reply: a cut or a recut.</summary>
    /// <param name="Scope">Where and when it happened.</param>
    /// <param name="HeardText">The words the caller heard before the cut. The text is the caller's conversation text: a hook that logs it logs personal data.</param>
    /// <param name="Played">How long the reply played, or <see langword="null"/> when unknown.</param>
    /// <param name="AmendsEventId">The <see cref="HookNotice.EventId"/> of the <see cref="TurnCompleted"/> notice this cut amends.</param>
    public sealed record ReplyCut(
        HookScope Scope,
        string HeardText,
        TimeSpan? Played,
        Guid AmendsEventId)
        : HookNotice(Scope);
}
