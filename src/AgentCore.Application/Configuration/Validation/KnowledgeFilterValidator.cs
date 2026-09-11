using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;

namespace AgentCore.Application.Configuration.Validation;

/// <summary>
/// Refuses a <c>scope.filterable</c> block the search tool could not be built from.
/// </summary>
internal static class KnowledgeFilterValidator
{
    private const string Pointer = "/providers/knowledge/scope/filterable";

    /// <summary>Checks the block, adding one error per fault.</summary>
    /// <param name="scope">The document's <c>providers.knowledge.scope</c> block, or <see langword="null"/>.</param>
    /// <param name="errors">Where the faults go.</param>
    /// <exception cref="ArgumentNullException"><paramref name="errors"/> is <see langword="null"/>.</exception>
    internal static void Check(KnowledgeScopeConfiguration? scope, List<ConfigurationError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);

        if (scope?.Filterable is not { Count: > 0 } filterable)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(scope.Template))
        {
            errors.Add(Reference(
                Pointer,
                "filterable names facets and scope.template is not set, so there is no payload path "
                + "to resolve a facet key to. Set the template, or drop filterable."));
        }

        HashSet<string> seen = new(StringComparer.Ordinal);

        for (var index = 0; index < filterable.Count; index++)
        {
            var facet = filterable[index];
            var pointer = ConfigurationError.AppendPointer(Pointer, index);

            if (string.IsNullOrWhiteSpace(facet.Key))
            {
                errors.Add(Reference(pointer, "the facet key is blank, so it names no facet."));
            }
            else if (!seen.Add(facet.Key))
            {
                errors.Add(Reference(
                    pointer,
                    $"the facet key '{facet.Key}' is named twice. One key is one choice in the "
                    + "search tool's filters argument, and a repeat offers the model the same choice "
                    + "again."));
            }

            if (string.IsNullOrWhiteSpace(facet.Description))
            {
                errors.Add(Reference(
                    pointer,
                    $"the facet '{facet.Key}' has no description. The description is the only thing "
                    + "the model reads before it picks a value, so a facet without one is a key it "
                    + "can only guess at."));
            }

            if (facet.Resolve is { } resolve)
            {
                CheckResolve(facet, resolve, filterable, ConfigurationError.AppendPointer(pointer, "resolve"), errors);
            }
        }
    }

    /// <summary>
    /// The lookup runs one hop: a facet resolves through the cards of another facet, and that one
    /// must be pickable from its description alone, or the model would chase a chain of lookups.
    /// </summary>
    private static void CheckResolve(
        KnowledgeFilterableFacetConfiguration facet,
        KnowledgeFacetResolveConfiguration resolve,
        IReadOnlyList<KnowledgeFilterableFacetConfiguration> filterable,
        string pointer,
        List<ConfigurationError> errors)
    {
        var via = ConfigurationError.AppendPointer(pointer, "via");
        var viaKey = ConfigurationError.AppendPointer(via, "key");

        if (string.Equals(resolve.Via.Key, facet.Key, StringComparison.Ordinal))
        {
            errors.Add(Reference(
                viaKey,
                $"the facet '{facet.Key}' resolves through itself. The lookup must search the cards "
                + "of another facet, one whose value the model can pick without a lookup."));
        }
        else if (filterable.FirstOrDefault(other => string.Equals(other.Key, resolve.Via.Key, StringComparison.Ordinal))
            is not { } index)
        {
            errors.Add(Reference(
                viaKey,
                $"the facet '{facet.Key}' resolves through '{resolve.Via.Key}', which is not a "
                + "filterable facet. The lookup is a filtered search, so the facet it filters on must "
                + "be one the model may set."));
        }
        else if (index.Resolve is not null)
        {
            errors.Add(Reference(
                viaKey,
                $"the facet '{facet.Key}' resolves through '{index.Key}', which resolves through "
                + "something else in turn. One hop only: the facet a lookup filters on must be "
                + "pickable from its description alone."));
        }

        if (string.IsNullOrWhiteSpace(resolve.Via.Value))
        {
            errors.Add(Reference(
                ConfigurationError.AppendPointer(via, "value"),
                $"the facet '{facet.Key}' resolves through '{resolve.Via.Key}' with a blank value, so "
                + "the lookup would filter on nothing."));
        }

        if (string.IsNullOrWhiteSpace(resolve.Query))
        {
            errors.Add(Reference(
                ConfigurationError.AppendPointer(pointer, "query"),
                $"the facet '{facet.Key}' has a blank resolve query, so the model has nothing to search for."));
        }

        if (string.IsNullOrWhiteSpace(resolve.Read))
        {
            errors.Add(Reference(
                ConfigurationError.AppendPointer(pointer, "read"),
                $"the facet '{facet.Key}' does not say where on the card the value is read from."));
        }
    }

    private static ConfigurationError Reference(string pointer, string message)
        => new() { Pointer = pointer, Message = message, Check = ConfigurationCheck.ReferenceResolution };
}
