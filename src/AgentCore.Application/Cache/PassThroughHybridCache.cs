using Microsoft.Extensions.Caching.Hybrid;

namespace AgentCore.Application.Cache
{
    /// <summary>
    /// The cache a container runs on when the host set none: it stores nothing, so every read runs
    /// its factory and every write and removal is a no-op.
    /// </summary>
    public sealed class PassThroughHybridCache : HybridCache
    {
        /// <summary>Gets the one instance; it holds no state, so one is enough.</summary>
        public static PassThroughHybridCache Instance { get; } = new();

        private PassThroughHybridCache()
        {
        }

        /// <inheritdoc/>
        public override ValueTask<T> GetOrCreateAsync<TState, T>(
            string key,
            TState state,
            Func<TState, CancellationToken, ValueTask<T>> factory,
            HybridCacheEntryOptions? options = null,
            IEnumerable<string>? tags = null,
            CancellationToken cancellationToken = default)
        {
            return factory(state, cancellationToken);
        }

        /// <inheritdoc/>
        public override ValueTask SetAsync<T>(
            string key,
            T value,
            HybridCacheEntryOptions? options = null,
            IEnumerable<string>? tags = null,
            CancellationToken cancellationToken = default)
        {
            return default;
        }

        /// <inheritdoc/>
        public override ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            return default;
        }

        /// <inheritdoc/>
        public override ValueTask RemoveByTagAsync(string tag, CancellationToken cancellationToken = default)
        {
            return default;
        }
    }
}
