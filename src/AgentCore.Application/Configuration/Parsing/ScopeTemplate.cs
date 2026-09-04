namespace AgentCore.Application.Configuration.Parsing;

/// <summary>
/// A <c>providers.knowledge.scope.template</c>: the payload path one facet key becomes.
/// </summary>
/// <remarks>
/// There is no default template, because a template is a claim about where one particular collection
/// keeps its facets. Boot, the probe (§8 step 5) and the store's own filter all resolve a key the
/// same way, so they resolve it through this one type rather than each holding the raw string.
/// </remarks>
public sealed record ScopeTemplate
{
    /// <summary>The placeholder a template writes where the facet key goes.</summary>
    public const string KeyPlaceholder = "{key}";

    /// <summary>The advice every refusal over a missing template ends with.</summary>
    public const string WriteOneAdvice =
        "There is no default: write scope.template as the payload path one facet key becomes, such "
        + "as '{key}' for flat facets or 'facets.{key}' for facets nested under one struct.";

    private ScopeTemplate(string raw) => Raw = raw;

    /// <summary>Gets the text exactly as the document wrote it.</summary>
    public string Raw { get; }

    /// <summary>Reads a template out of what the document wrote.</summary>
    /// <param name="text">The raw <c>scope.template</c> value.</param>
    /// <returns>The template, or <see langword="null"/> when the document names none.</returns>
    public static ScopeTemplate? Parse(string? text)
        => text is { Length: > 0 } ? new ScopeTemplate(text) : null;

    /// <summary>Turns one facet key into the payload path this template names for it.</summary>
    /// <param name="key">The facet key: a slot name, or a <c>wildcard.facets</c> member.</param>
    /// <returns>The resolved path.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
    public string Resolve(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        return Raw.Replace(KeyPlaceholder, key, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public override string ToString() => Raw;
}
