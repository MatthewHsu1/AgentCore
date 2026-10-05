namespace AgentCore.Application.Hooks.Notices
{
    /// <summary>Moderation decided about the caller's words.</summary>
    /// <param name="Scope">Where and when it happened.</param>
    /// <param name="Verdict">What moderation decided.</param>
    /// <param name="Categories">The endpoint's categories in its own order; empty unless <see cref="InputVerdict.Flagged"/>.</param>
    /// <param name="Reason">Why there was no verdict when <paramref name="Verdict"/> is <see cref="InputVerdict.Unavailable"/>, otherwise <see langword="null"/>.</param>
    public sealed record InputModerated(
        HookScope Scope,
        InputVerdict Verdict,
        IReadOnlyList<string> Categories,
        ModerationUnavailableReason? Reason)
        : HookNotice(Scope);
}
