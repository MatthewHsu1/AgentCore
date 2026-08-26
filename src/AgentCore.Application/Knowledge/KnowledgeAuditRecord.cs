using AgentCore.Application.Configuration.Schema;
using AgentCore.Domain.Knowledge;

namespace AgentCore.Application.Knowledge;

/// <summary>
/// What one retrieval actually did — the reader is the on-call engineer, and the model never sees it.
/// </summary>
internal sealed record KnowledgeAuditRecord
{
    private static readonly IReadOnlyDictionary<string, string> EmptyScope =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Gets the turn the retrieval ran inside, or <see langword="null"/> when nothing names it.</summary>
    public string? TurnId { get; init; }

    /// <summary>Gets the id of the agent that asked.</summary>
    public required string Agent { get; init; }

    /// <summary>Gets the mode the agent's <c>knowledge:</c> block declared.</summary>
    public required KnowledgeMode Mode { get; init; }

    /// <summary>Gets the search text.</summary>
    public required string Query { get; init; }

    /// <summary>Gets the facets the turn's scope carried, or empty when none was open.</summary>
    public required IReadOnlyDictionary<string, string> Scope { get; init; }

    /// <summary>Gets how long the whole retrieval took, in milliseconds.</summary>
    public required double LatencyMs { get; init; }

    /// <summary>Gets the cards the search returned, ranked cards first.</summary>
    public required IReadOnlyList<CardEntry> Cards { get; init; }

    /// <summary>Gets what the port threw, or <see langword="null"/> when the search succeeded.</summary>
    public string? Failure { get; init; }

    /// <summary>Builds the record of one retrieval.</summary>
    /// <param name="turnId">The turn the retrieval ran inside, or <see langword="null"/> when nothing names it.</param>
    /// <param name="agent">The id of the agent that asked.</param>
    /// <param name="mode">The mode the agent's <c>knowledge:</c> block declared.</param>
    /// <param name="query">The search text.</param>
    /// <param name="scope">The turn's open scope, or <see langword="null"/> when none was open.</param>
    /// <param name="cards">The cards the search returned, ranked cards first.</param>
    /// <param name="latencyMs">How long the whole retrieval took, in milliseconds.</param>
    /// <param name="failure">What the port threw, or <see langword="null"/> when it succeeded.</param>
    /// <returns>The record.</returns>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    public static KnowledgeAuditRecord For(
        string? turnId,
        string agent,
        KnowledgeMode mode,
        string query,
        KnowledgeScope? scope,
        IReadOnlyList<KnowledgeCard> cards,
        double latencyMs,
        Exception? failure)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(cards);

        return new KnowledgeAuditRecord
        {
            TurnId = turnId,
            Agent = agent,
            Mode = mode,
            Query = query,
            Scope = scope?.Facets ?? EmptyScope,
            LatencyMs = latencyMs,
            Cards = [.. cards.Select(CardEntry.For)],
            Failure = failure?.ToString(),
        };
    }

    /// <summary>Reads this record into the part of it a log store may hold.</summary>
    /// <returns>The view. It carries no search text.</returns>
    public LogView ForLog() => new()
    {
        TurnId = TurnId,
        Agent = Agent,
        Mode = Mode,
        QueryLength = Query.Length,
        Scope = Scope,
        LatencyMs = LatencyMs,
        Cards = Cards,
    };

    /// <summary>
    /// The part of one retrieval that may be written to a log store.
    /// </summary>
    internal sealed record LogView
    {
        /// <summary>Gets the turn the retrieval ran inside, or <see langword="null"/> when nothing names it.</summary>
        public string? TurnId { get; init; }

        /// <summary>Gets the id of the agent that asked.</summary>
        public required string Agent { get; init; }

        /// <summary>Gets the mode the agent's <c>knowledge:</c> block declared.</summary>
        public required KnowledgeMode Mode { get; init; }

        /// <summary>Gets how many characters the search input held. Never the input itself.</summary>
        public required int QueryLength { get; init; }

        /// <summary>Gets the facets the turn's scope carried, or empty when none was open.</summary>
        public required IReadOnlyDictionary<string, string> Scope { get; init; }

        /// <summary>Gets how long the whole retrieval took, in milliseconds.</summary>
        public required double LatencyMs { get; init; }

        /// <summary>Gets the cards the search returned, ranked cards first. Never a card body.</summary>
        public required IReadOnlyList<CardEntry> Cards { get; init; }
    }

    /// <summary>One card, as the audit record keeps it.</summary>
    internal sealed record CardEntry
    {
        /// <summary>Gets the card id.</summary>
        public required string CardId { get; init; }

        /// <summary>Gets the fused score, or <see langword="null"/> when a link pulled the card in.</summary>
        public double? Score { get; init; }

        /// <summary>Gets how much the source is trusted: 3 a manual, 2 a note, 1 an email.</summary>
        public required int Authority { get; init; }

        /// <summary>Gets the manifest row the card came from.</summary>
        public required string SourceRef { get; init; }

        /// <summary>Gets where in that source it sits.</summary>
        public required string Locator { get; init; }

        /// <summary>Gets <c>"ranked"</c> when the search scored the card, or <c>"see_also"</c> when a link pulled it in.</summary>
        public required string Via { get; init; }

        /// <summary>Reads one card into its audit entry.</summary>
        /// <param name="card">The card to record.</param>
        /// <returns>The entry.</returns>
        internal static CardEntry For(KnowledgeCard card) => new()
        {
            CardId = card.CardId,
            Score = card.Score,
            Authority = card.Authority,
            SourceRef = card.SourceRef,
            Locator = card.SourceLocator,
            Via = card.ViaLink ? "see_also" : "ranked",
        };
    }
}
