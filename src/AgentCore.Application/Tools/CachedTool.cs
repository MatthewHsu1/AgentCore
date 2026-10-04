using System.Text.Json.Nodes;
using AgentCore.Application.Hooks.Layers;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Hybrid;

namespace AgentCore.Application.Tools
{
    /// <summary>
    /// One tool whose answer is served again, for a while, to a repeat of the same arguments.
    /// </summary>
    public sealed class CachedTool : DelegatingAIFunction
    {
        private const string KeyPrefix = "agentcore:tool:";

        private static readonly HybridCacheEntryOptions ReadOnly = new()
        {
            Flags = HybridCacheEntryFlags.DisableUnderlyingData,
        };

        private readonly HybridCache _cache;

        private readonly HybridCacheEntryOptions _write;

        /// <summary>Creates the wrapper.</summary>
        /// <param name="inner">The tool whose answers are kept.</param>
        /// <param name="cache">The host's cache.</param>
        /// <param name="lifetime">How long one answer is served again. It must be positive.</param>
        public CachedTool(AIFunction inner, HybridCache cache, TimeSpan lifetime)
            : base(inner)
        {
            ArgumentNullException.ThrowIfNull(cache);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero);

            _cache = cache;
            _write = new HybridCacheEntryOptions { Expiration = lifetime };
        }

        /// <inheritdoc />
        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(arguments);

            string key = CacheKey(arguments);

            string? kept = await _cache
                .GetOrCreateAsync(key, static _ => ValueTask.FromResult<string?>(null), ReadOnly, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (kept is not null)
            {
                ToolCallOutcomes.Mark(arguments, ToolCallOutcomes.CachedKey);
                return JsonNode.Parse(kept);
            }

            object? result = await base.InvokeCoreAsync(arguments, cancellationToken).ConfigureAwait(false);

            if (ToolResultJson.ToNode(result) is { } node && !ToolErrorResult.IsError(node))
            {
                await _cache.SetAsync(key, node.ToJsonString(), _write, cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            return result;
        }

        /// <summary>The key reads the tool name and the model's arguments, in a fixed order. The side channel (<see cref="AIFunctionArguments.Context"/>) is not part of the call.</summary>
        private string CacheKey(AIFunctionArguments arguments)
        {
            object?[][] ordered = [.. arguments
                .OrderBy(argument => argument.Key, StringComparer.Ordinal)
                .Select(argument => new[] { argument.Key, argument.Value })];

            return KeyPrefix + AIJsonUtilities.HashDataToString([Name, ordered], AIJsonUtilities.DefaultOptions);
        }
    }
}
