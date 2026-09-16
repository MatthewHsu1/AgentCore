using System.Text.Json;

using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Runtime;

using Microsoft.Extensions.AI;
using Microsoft.Agents.AI;

namespace AgentCore.Application.Knowledge;

/// <summary>
/// The framework's search function, with a <c>filters</c> argument in front of it.
/// </summary>
internal sealed class FacetFilteredSearch : DelegatingAIFunction
{
    private readonly IReadOnlyList<KnowledgeFilterableFacetConfiguration>? _facets;

    private readonly KnowledgeSearch.Core _core;

    private readonly JsonElement _schema;

    /// <summary>Wraps one search function.</summary>
    /// <param name="innerFunction">The function the framework built: the schema donor, never invoked.</param>
    /// <param name="facets">What <c>scope.filterable</c> declared, in document order — or null when it declared none.</param>
    /// <param name="core">The search bound to this agent's store and wiring.</param>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    internal FacetFilteredSearch(
        AIFunction innerFunction,
        IReadOnlyList<KnowledgeFilterableFacetConfiguration>? facets,
        KnowledgeSearch.Core core)
        : base(innerFunction)
    {
        ArgumentNullException.ThrowIfNull(innerFunction);
        ArgumentNullException.ThrowIfNull(core);

        _facets = facets;
        _core = core;
        _schema = facets is { Count: > 0 }
            ? FacetFilterSchema.Extend(innerFunction.JsonSchema, facets)
            : innerFunction.JsonSchema;
    }

    /// <inheritdoc />
    public override JsonElement JsonSchema => _schema;

    /// <inheritdoc />
    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var named = _facets is { Count: > 0 }
            ? FacetFilterReader.Read(arguments, _facets)
            : null;

        var turn = arguments.TryGetValue(TurnInvocation.ArgumentsKey, out var filed) && filed is TurnInvocation invocation
            ? invocation
            : null;

        // The donor's own declaration names the question: the model was built against it.
        var question = FirstQueryArgument(InnerFunction.JsonSchema) is { } name
            && arguments.TryGetValue(name, out var asked)
            ? QueryText(asked)
            : null;

        if (question is null)
        {
            throw new InvalidOperationException(
                "The knowledge search was called without a search question, so there is nothing to look up.");
        }

        IReadOnlyList<TextSearchProvider.TextSearchResult> results =
            await _core(question, named?.Count > 0 ? named : null, turn, cancellationToken).ConfigureAwait(false);

        return results;
    }

    /// <summary>Reads the donor's question argument name off its own schema.</summary>
    private static string? FirstQueryArgument(JsonElement schema)
    {
        if (schema.ValueKind is not JsonValueKind.Object
            || !schema.TryGetProperty("properties", out var properties)
            || properties.ValueKind is not JsonValueKind.Object)
        {
            return null;
        }

        return properties.EnumerateObject().Select(property => property.Name).FirstOrDefault();
    }

    private static string? QueryText(object? value)
        => value switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => null,
        };
}
