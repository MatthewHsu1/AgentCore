using AgentCore.Domain.Knowledge;

namespace AgentCore.Application.Hooks.Notices
{
    /// <summary>A knowledge search finished.</summary>
    /// <param name="Scope">Where and when it happened.</param>
    /// <param name="Query">The query text. The text is the caller's conversation text: a hook that logs it logs personal data.</param>
    /// <param name="SearchScope">The scope the search ran under, or <see langword="null"/>.</param>
    /// <param name="Hits">How many hits came back.</param>
    /// <param name="Latency">How long the search took.</param>
    /// <param name="Failure">The failure message, or <see langword="null"/> when the search succeeded.</param>
    public sealed record KnowledgeSearched(
        HookScope Scope,
        string Query,
        KnowledgeScope? SearchScope,
        int Hits,
        TimeSpan Latency,
        string? Failure)
        : HookNotice(Scope);
}
