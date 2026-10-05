namespace AgentCore.Application.Hooks.Notices
{
    /// <summary>
    /// One model round trip ended. A round a <see cref="AgentHook.BeforeModelAsync"/> hook answered never reached the
    /// model, so it raises none.
    /// </summary>
    /// <param name="Scope">Where and when it happened.</param>
    /// <param name="ModelId">The model that answered, or <see langword="null"/> when unknown.</param>
    /// <param name="Round">The round within the turn.</param>
    /// <param name="InputTokens">Input tokens, or <see langword="null"/> when the provider reported none.</param>
    /// <param name="OutputTokens">Output tokens, or <see langword="null"/> when the provider reported none.</param>
    /// <param name="CachedInputTokens">Cached input tokens, or <see langword="null"/> when the provider reported none.</param>
    /// <param name="Latency">How long the round trip took.</param>
    /// <param name="FinishReason">The provider's finish reason, or <see langword="null"/>.</param>
    /// <param name="Failure">
    /// The failure message, or <see langword="null"/> when the model answered. A round an
    /// <see cref="AgentHook.AfterModelFailedAsync"/> hook answered carries the model's failure it answered for.
    /// </param>
    public sealed record ModelCalled(
        HookScope Scope,
        string? ModelId,
        int Round,
        long? InputTokens,
        long? OutputTokens,
        long? CachedInputTokens,
        TimeSpan Latency,
        string? FinishReason,
        string? Failure)
        : HookNotice(Scope);
}
