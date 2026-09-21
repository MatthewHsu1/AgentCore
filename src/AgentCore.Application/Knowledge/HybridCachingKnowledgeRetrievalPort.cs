using AgentCore.Application.Ports;
using AgentCore.Domain.Knowledge;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Hybrid;

namespace AgentCore.Application.Knowledge
{
    /// <summary>
    /// Remembers each search's cards in the host's cache, so a repeat of the same query under the same
    /// scope skips the store. A store answers the same way to the same question until it is re-indexed,
    /// so the lifetime is short by default: a stale answer costs a wrong card, not a wrong embedding.
    /// </summary>
    public sealed class HybridCachingKnowledgeRetrievalPort : IKnowledgeRetrievalPort, IAsyncDisposable
    {
        /// <summary>How long an entry lives when the host names no lifetime.</summary>
        public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(5);

        private const string KeyPrefix = "agentcore:knowledge:";

        private readonly IKnowledgeRetrievalPort _inner;

        private readonly HybridCache _cache;

        private readonly HybridCacheEntryOptions _entry;

        /// <summary>Wraps one store in the cache.</summary>
        /// <param name="inner">The store that runs the search.</param>
        /// <param name="cache">The host's cache.</param>
        /// <param name="lifetime">How long one answer stays cached, or <see langword="null"/> for <see cref="DefaultLifetime"/>.</param>
        /// <exception cref="ArgumentNullException">The store or the cache is <see langword="null"/>.</exception>
        public HybridCachingKnowledgeRetrievalPort(
            IKnowledgeRetrievalPort inner,
            HybridCache cache,
            TimeSpan? lifetime = null)
        {
            ArgumentNullException.ThrowIfNull(inner);
            ArgumentNullException.ThrowIfNull(cache);

            _inner = inner;
            _cache = cache;
            _entry = new HybridCacheEntryOptions { Expiration = lifetime ?? DefaultLifetime };
        }

        /// <inheritdoc/>
        /// <remarks>
        /// The store's own exceptions pass straight through and cache nothing, so a store that is down
        /// or a scope it refuses reads exactly as it does without the cache.
        /// </remarks>
        public async ValueTask<IReadOnlyList<KnowledgeCard>> SearchAsync(
            string query,
            KnowledgeScope? scope = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(query);

            // The cards ride as one JSON string so the host's serializer configuration never sees them:
            // Extras is an object tree, and only KnowledgeCardCodec knows how to read it back.
            string json = await _cache
                .GetOrCreateAsync(
                    CacheKey(query, scope),
                    (Port: _inner, Query: query, Scope: scope),
                    static async (state, token) => KnowledgeCardCodec.Encode(
                        await state.Port.SearchAsync(state.Query, state.Scope, token).ConfigureAwait(false)),
                    _entry,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return KnowledgeCardCodec.Decode(json);
        }

        /// <inheritdoc/>
        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            ArgumentNullException.ThrowIfNull(serviceType);

            return serviceKey is null && serviceType.IsInstanceOfType(this)
                ? this
                : _inner.GetService(serviceType, serviceKey);
        }

        /// <summary>Closes the store this wraps.</summary>
        public async ValueTask DisposeAsync()
        {
            switch (_inner)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
                default:
                    break;
            }
        }

        private static string CacheKey(string query, KnowledgeScope? scope)
        {
            string[][] facets = scope is null
                ? []
                : [.. scope.Facets
                    .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                    .Select(entry => new[] { entry.Key, entry.Value })];

            return KeyPrefix + AIJsonUtilities.HashDataToString([query, facets], AIJsonUtilities.DefaultOptions);
        }
    }
}
