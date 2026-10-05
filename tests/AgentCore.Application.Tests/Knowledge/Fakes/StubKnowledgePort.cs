using System.Diagnostics;
using AgentCore.Application.Ports;
using AgentCore.Domain.Knowledge;
using AgentCore.Application.Runtime.Clarification;

namespace AgentCore.Application.Tests.Knowledge.Fakes
{
    /// <summary>A store that answers every query with the same cards, and records what it was asked.</summary>
    internal sealed class StubKnowledgePort(IReadOnlyList<KnowledgeCard> cards) : IKnowledgeRetrievalPort
    {
        private readonly IReadOnlyList<KnowledgeCard> _cards = cards;

        /// <summary>Gets the query of the last search, or <see langword="null"/> when none ran.</summary>
        public string? LastQuery { get; private set; }

        /// <summary>Gets how many searches reached this store.</summary>
        public int Calls { get; private set; }

        /// <summary>Gets the scope the last search was handed, or <see langword="null"/> when none ran.</summary>
        public KnowledgeScope? ScopeAtTheStore { get; private set; }

        /// <summary>Always <see langword="null"/>: the store is handed a scope, never the turn's holder.</summary>
        public Clarifications? ClarificationsAtTheStore { get; private set; }

        public ValueTask<IReadOnlyList<KnowledgeCard>> SearchAsync(
            string query,
            KnowledgeScope? scope = null,
            CancellationToken cancellationToken = default)
        {
            LastQuery = query;
            ScopeAtTheStore = scope;
            Calls++;
            return ValueTask.FromResult(_cards);
        }
    }

    /// <summary>A store that is down.</summary>
    internal sealed class ThrowingKnowledgePort(Exception failure) : IKnowledgeRetrievalPort
    {
        private readonly Exception _failure = failure;

        public ValueTask<IReadOnlyList<KnowledgeCard>> SearchAsync(
            string query,
            KnowledgeScope? scope = null,
            CancellationToken cancellationToken = default)
        {
            throw _failure;
        }
    }

    /// <summary>
    /// A store that hangs, with a deadline of its own linked into the caller's token.
    /// </summary>
    internal sealed class HangingKnowledgePort(TimeSpan deadline) : IKnowledgeRetrievalPort
    {
        private readonly TimeSpan _deadline = deadline;

        public async ValueTask<IReadOnlyList<KnowledgeCard>> SearchAsync(
            string query,
            KnowledgeScope? scope = null,
            CancellationToken cancellationToken = default)
        {
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(_deadline);

            await Task.Delay(Timeout.Infinite, deadline.Token).ConfigureAwait(false);

            throw new UnreachableException();
        }
    }

    /// <summary>
    /// A store that filters the way the real one does: it folds the passed scope's facets into its answer.
    /// </summary>
    internal sealed class ScopeFilteringKnowledgePort(
        params (KnowledgeCard Card, IReadOnlyDictionary<string, string> Facets)[] corpus) : IKnowledgeRetrievalPort
    {
        private static readonly IReadOnlyDictionary<string, string> NoFacets =
            new Dictionary<string, string>(StringComparer.Ordinal);

        private readonly IReadOnlyList<(KnowledgeCard Card, IReadOnlyDictionary<string, string> Facets)> _corpus = corpus;

        public ValueTask<IReadOnlyList<KnowledgeCard>> SearchAsync(
            string query,
            KnowledgeScope? scope = null,
            CancellationToken cancellationToken = default)
        {
            IReadOnlyDictionary<string, string> wanted = scope?.Facets ?? NoFacets;

            IReadOnlyList<KnowledgeCard> hits =
            [
                .. _corpus
                    .Where(entry => wanted.All(facet =>
                        entry.Facets.TryGetValue(facet.Key, out string? held)
                        && string.Equals(held, facet.Value, StringComparison.Ordinal)))
                    .Select(entry => entry.Card),
            ];

            return ValueTask.FromResult(hits);
        }
    }
}
