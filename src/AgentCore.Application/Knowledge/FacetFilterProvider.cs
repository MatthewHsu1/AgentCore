using AgentCore.Application.Configuration.Schema;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Knowledge;

/// <summary>
/// Puts a <c>filters</c> argument on whichever search function the inner provider offers.
/// </summary>
internal sealed class FacetFilterProvider(
    AIContextProvider innerProvider,
    IReadOnlyList<KnowledgeFilterableFacetConfiguration>? facets,
    KnowledgeSearch.Core core)
    : AIContextProvider
{
    private readonly AIContextProvider _inner = innerProvider
        ?? throw new ArgumentNullException(nameof(innerProvider));

    private readonly IReadOnlyList<KnowledgeFilterableFacetConfiguration>? _facets = facets;

    private readonly KnowledgeSearch.Core _core = core
        ?? throw new ArgumentNullException(nameof(core));

    /// <inheritdoc />
    public override IReadOnlyList<string> StateKeys => _inner.StateKeys;

    /// <inheritdoc />
    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);

        return serviceType.IsInstanceOfType(this) && serviceKey is null
            ? this
            : _inner.GetService(serviceType, serviceKey);
    }

    /// <inheritdoc />
    protected override async ValueTask<AIContext> InvokingCoreAsync(
        InvokingContext context, CancellationToken cancellationToken = default)
    {
        HashSet<AITool>? existing = context.AIContext.Tools is { } input
            ? [.. input]
            : null;

        var provided = await _inner.InvokingAsync(context, cancellationToken).ConfigureAwait(false);

        if (provided.Tools is not { } tools)
        {
            return provided;
        }

        List<AITool> wrapped = [];

        foreach (var tool in tools)
        {
            wrapped.Add(tool is AIFunction function && existing?.Contains(tool) is not true
                ? new FacetFilteredSearch(function, _facets, _core)
                : tool);
        }

        provided.Tools = wrapped;

        return provided;
    }

    /// <inheritdoc />
    protected override ValueTask InvokedCoreAsync(
        InvokedContext context, CancellationToken cancellationToken = default)
        => _inner.InvokedAsync(context, cancellationToken);
}
