using System.Text.Json;

using AgentCore.Application.Configuration.Schema;

using Microsoft.Extensions.AI;

namespace AgentCore.Application.Knowledge;

/// <summary>
/// The framework's search function, with a <c>filters</c> argument in front of it.
/// </summary>
internal sealed class FacetFilteredSearch : DelegatingAIFunction
{
    private readonly IReadOnlyList<KnowledgeFilterableFacetConfiguration> _facets;

    private readonly JsonElement _schema;

    /// <summary>Wraps one search function.</summary>
    /// <param name="innerFunction">The function the framework built.</param>
    /// <param name="facets">What <c>scope.filterable</c> declared, in document order.</param>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    internal FacetFilteredSearch(
        AIFunction innerFunction,
        IReadOnlyList<KnowledgeFilterableFacetConfiguration> facets)
        : base(innerFunction)
    {
        ArgumentNullException.ThrowIfNull(innerFunction);
        ArgumentNullException.ThrowIfNull(facets);

        _facets = facets;
        _schema = FacetFilterSchema.Extend(innerFunction.JsonSchema, facets);
    }

    /// <inheritdoc />
    public override JsonElement JsonSchema => _schema;

    /// <inheritdoc />
    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var named = FacetFilterReader.Read(arguments, _facets);

        using var scope = named.Count > 0 ? ToolFacetScope.Open(named) : null;

        return await base.InvokeCoreAsync(Forwarded(arguments), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The arguments without the one this wrapper owns.</summary>
    private static AIFunctionArguments Forwarded(AIFunctionArguments arguments)
    {
        if (!arguments.ContainsKey(FacetFilterSchema.ArgumentName))
        {
            return arguments;
        }

        Dictionary<string, object?> kept = new(StringComparer.Ordinal);

        foreach (var (name, value) in arguments)
        {
            if (!string.Equals(name, FacetFilterSchema.ArgumentName, StringComparison.Ordinal))
            {
                kept[name] = value;
            }
        }

        return new AIFunctionArguments(kept)
        {
            Services = arguments.Services,
            Context = arguments.Context,
        };
    }
}
