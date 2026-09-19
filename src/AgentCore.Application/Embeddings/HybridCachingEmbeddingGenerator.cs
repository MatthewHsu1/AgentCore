using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Hybrid;

namespace AgentCore.Application.Embeddings;

/// <summary>
/// Remembers each text's embedding in the host's cache, so a repeat of the same text under the same
/// model skips the vendor call. The framework's base class wants a plain read and a plain write;
/// <see cref="HybridCache"/> has no plain read, so the read is a create that is told not to create.
/// </summary>
public sealed class HybridCachingEmbeddingGenerator : CachingEmbeddingGenerator<string, Embedding<float>>
{
    /// <summary>How long an entry lives when the host names no lifetime.</summary>
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromDays(1);

    private const string KeyPrefix = "agentcore:embedding:";

    private static readonly HybridCacheEntryOptions ReadOnly = new()
    {
        Flags = HybridCacheEntryFlags.DisableUnderlyingData,
    };

    private readonly HybridCache _cache;

    private readonly HybridCacheEntryOptions _write;

    private readonly string? _defaultModel;

    /// <summary>Wraps one generator in the cache.</summary>
    /// <param name="inner">The generator that talks to the vendor.</param>
    /// <param name="cache">The host's cache.</param>
    /// <param name="lifetime">How long one embedding stays cached, or <see langword="null"/> for <see cref="DefaultLifetime"/>.</param>
    public HybridCachingEmbeddingGenerator(
        IEmbeddingGenerator<string, Embedding<float>> inner,
        HybridCache cache,
        TimeSpan? lifetime = null)
        : base(inner)
    {
        ArgumentNullException.ThrowIfNull(cache);

        _cache = cache;
        _write = new HybridCacheEntryOptions { Expiration = lifetime ?? DefaultLifetime };
        _defaultModel = inner.GetService<EmbeddingGeneratorMetadata>()?.DefaultModelId;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The base hands in the text and the call's options. The options may name no model, in which
    /// case the vendor falls back to the generator's default, so that default joins the key.
    /// </remarks>
    protected override string GetCacheKey(params ReadOnlySpan<object?> values)
    {
        object?[] parts = [.. values, _defaultModel];
        return KeyPrefix + AIJsonUtilities.HashDataToString(parts, AIJsonUtilities.DefaultOptions);
    }

    /// <inheritdoc/>
    protected override async Task<Embedding<float>?> ReadCacheAsync(string key, CancellationToken cancellationToken)
        => await _cache
            .GetOrCreateAsync(key, static _ => ValueTask.FromResult<Embedding<float>?>(null), ReadOnly, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

    /// <inheritdoc/>
    protected override Task WriteCacheAsync(string key, Embedding<float> value, CancellationToken cancellationToken)
        => _cache.SetAsync(key, value, _write, cancellationToken: cancellationToken).AsTask();
}
