using System.Diagnostics;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Domain.Knowledge;

namespace AgentCore.Application.Tests.Knowledge.Fakes;

/// <summary>A store that answers every query with the same cards, and records what it was asked.</summary>
internal sealed class StubKnowledgePort : IKnowledgeRetrievalPort
{
    private readonly IReadOnlyList<KnowledgeCard> _cards;

    public StubKnowledgePort(IReadOnlyList<KnowledgeCard> cards)
        => _cards = cards;

    /// <summary>Gets the query of the last search, or <see langword="null"/> when none ran.</summary>
    public string? LastQuery { get; private set; }

    /// <summary>Gets how many searches reached this store.</summary>
    public int Calls { get; private set; }

    /// <summary>Gets the scope ambient on the flow when the last search arrived, or <see langword="null"/>.</summary>
    /// <remarks>
    /// Read here rather than in the test body on purpose: the real store reads the ambient at exactly
    /// this point, deep inside the provider's delegate and several awaits below wherever the host
    /// opened it. A test that reads it anywhere else proves something easier.
    /// </remarks>
    public KnowledgeScope? ScopeAtTheStore { get; private set; }

    public ValueTask<IReadOnlyList<KnowledgeCard>> SearchAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        LastQuery = query;
        ScopeAtTheStore = KnowledgeScopeScope.Current;
        Calls++;
        return ValueTask.FromResult(_cards);
    }
}

/// <summary>A store that is down.</summary>
internal sealed class ThrowingKnowledgePort : IKnowledgeRetrievalPort
{
    private readonly Exception _failure;

    public ThrowingKnowledgePort(Exception failure)
        => _failure = failure;

    public ValueTask<IReadOnlyList<KnowledgeCard>> SearchAsync(
        string query,
        CancellationToken cancellationToken = default)
        => throw _failure;
}

/// <summary>
/// A store that hangs, with a deadline of its own linked into the caller's token.
/// </summary>
/// <remarks>
/// <para>
/// This is <c>QdrantKnowledgeStore</c>'s cancellation shape, reproduced rather than described: a
/// linked source, <c>CancelAfter</c>, and an await that ends when either side fires. Hand-throwing
/// an <see cref="OperationCanceledException"/> instead would let a test pass against a classifier
/// that read the exception's own token — which in the real store is the LINKED token on both paths,
/// and so tells the two cases apart in neither.
/// </para>
/// <para>
/// Whichever side fires, the exception is the same type and carries the same token. Only the
/// caller's own token differs between the two, which is what the provider must read.
/// </para>
/// </remarks>
internal sealed class HangingKnowledgePort : IKnowledgeRetrievalPort
{
    private readonly TimeSpan _deadline;

    public HangingKnowledgePort(TimeSpan deadline)
        => _deadline = deadline;

    public async ValueTask<IReadOnlyList<KnowledgeCard>> SearchAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_deadline);

        await Task.Delay(Timeout.Infinite, deadline.Token).ConfigureAwait(false);

        throw new UnreachableException();
    }
}

/// <summary>
/// A store that filters the way the real one does: it folds the ambient scope's facets into its answer.
/// </summary>
/// <remarks>
/// <see cref="StubKnowledgePort"/> ignores the scope, so it can prove which scope arrived but never
/// what the scope costs. This one answers differently under a different scope, which is what makes
/// "an unscoped agent still sees the whole corpus" a fact rather than a restatement of the wiring.
/// </remarks>
internal sealed class ScopeFilteringKnowledgePort : IKnowledgeRetrievalPort
{
    private static readonly IReadOnlyDictionary<string, string> NoFacets =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private readonly IReadOnlyList<(KnowledgeCard Card, IReadOnlyDictionary<string, string> Facets)> _corpus;

    public ScopeFilteringKnowledgePort(
        params (KnowledgeCard Card, IReadOnlyDictionary<string, string> Facets)[] corpus)
        => _corpus = corpus;

    public ValueTask<IReadOnlyList<KnowledgeCard>> SearchAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        var wanted = KnowledgeScopeScope.Current?.Facets ?? NoFacets;

        IReadOnlyList<KnowledgeCard> hits =
        [
            .. _corpus
                .Where(entry => wanted.All(facet =>
                    entry.Facets.TryGetValue(facet.Key, out var held)
                    && string.Equals(held, facet.Value, StringComparison.Ordinal)))
                .Select(entry => entry.Card),
        ];

        return ValueTask.FromResult(hits);
    }
}
