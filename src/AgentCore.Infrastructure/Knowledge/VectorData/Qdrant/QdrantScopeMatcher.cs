using AgentCore.Application.Configuration.Parsing;
using AgentCore.Domain.Knowledge;

using Google.Protobuf.Collections;

using Qdrant.Client.Grpc;

namespace AgentCore.Infrastructure.Knowledge.VectorData.Qdrant;

/// <summary>
/// What a <see cref="KnowledgeScope"/> means against one collection's payload.
/// </summary>
/// <remarks>
/// One scope is asked two different ways, and both have to agree. A search sends Qdrant a filter,
/// and Qdrant decides. A card fetched by a <c>see_also</c> link never passes that filter, so the
/// same decision is made here, in process, against the payload. Splitting those two readings apart
/// is how a linked card ends up in an answer the scope should have kept out.
/// </remarks>
/// <param name="options">The store's options, for the wildcard value and the facets it covers.</param>
/// <param name="template">
/// The parsed <c>scope.template</c>, or <see langword="null"/> when the document names none.
/// </param>
internal sealed class QdrantScopeMatcher(QdrantKnowledgeStoreOptions options, ScopeTemplate? template)
{
    /// <summary>The facets one scope names, in a fixed order.</summary>
    /// <param name="scope">The turn's scope, or <see langword="null"/> when nothing is scoped.</param>
    /// <returns>The facets, by key, ordinally ordered so one scope always builds one filter.</returns>
    public static IEnumerable<KeyValuePair<string, string>> Facets(KnowledgeScope? scope) =>
        scope is null ? [] : scope.Facets.OrderBy(entry => entry.Key, StringComparer.Ordinal);

    /// <summary>Turns one facet key into the payload path this collection keeps it at.</summary>
    /// <param name="facet">The facet key, as a scope names it.</param>
    /// <returns>The payload path.</returns>
    /// <remarks>
    /// Only reached once a scope names a facet, which is why an unset template is an error here and
    /// not at startup: a deployment whose agents never scope legitimately names no template.
    /// </remarks>
    /// <exception cref="InvalidOperationException">No <c>scope.template</c> was configured.</exception>
    public string Path(string facet)
        => template is { } resolved
            ? resolved.Resolve(facet)
            : throw new InvalidOperationException(
                $"a KnowledgeScope names the facet '{facet}' and providers.knowledge.scope.template is "
                + "unset, so AgentCore does not know what payload path that key becomes. "
                + ScopeTemplate.WriteOneAdvice);

    /// <summary>Builds the match one facet condition carries.</summary>
    /// <param name="facet">The facet key.</param>
    /// <param name="value">The value the turn's scope holds for it.</param>
    /// <returns>The match message.</returns>
    /// <remarks>
    /// Keyword and Keywords are different oneof cases, so a single-value Keywords list is a
    /// different message from today's. A deployment that configures no wildcard has to emit the
    /// message it emits now, or every stored query plan and every filter assertion changes under it.
    /// </remarks>
    public Match MatchFor(string facet, string value)
    {
        var values = Values(facet, value);

        return values.Count == 1
            ? new Match { Keyword = values[0] }
            : new Match { Keywords = new RepeatedStrings { Strings = { values } } };
    }

    /// <summary>Whether a card the ranking never chose is still inside the turn's scope.</summary>
    /// <param name="payload">The point's payload.</param>
    /// <param name="scope">The turn's scope, or <see langword="null"/> when nothing is scoped.</param>
    /// <returns><see langword="true"/> when every facet the scope names is satisfied.</returns>
    public bool Matches(MapField<string, Value> payload, KnowledgeScope? scope) =>
        Facets(scope).All(entry =>
            Holds(QdrantPayload.Read(payload, Path(entry.Key)), Values(entry.Key, entry.Value)));

    /// <summary>The values one facet accepts: its own, and the wildcard when this facet is named.</summary>
    private IReadOnlyList<string> Values(string facet, string value)
    {
        if (options.ScopeWildcard is not { Length: > 0 } wildcard
            || !options.ScopeWildcardFacets.Contains(facet, StringComparer.Ordinal)
            || string.Equals(value, wildcard, StringComparison.Ordinal))
        {
            return [value];
        }

        return [.. new[] { value, wildcard }.Order(StringComparer.Ordinal)];
    }

    /// <summary>Mirrors Qdrant keyword matching, where a list facet matches when any element does.</summary>
    private static bool Holds(Value? facet, IReadOnlyList<string> wanted) => facet switch
    {
        { KindCase: Value.KindOneofCase.StringValue } value =>
            wanted.Contains(value.StringValue, StringComparer.Ordinal),
        { KindCase: Value.KindOneofCase.ListValue } value =>
            value.ListValue.Values.Any(item => wanted.Contains(item.StringValue, StringComparer.Ordinal)),
        _ => false,
    };
}
