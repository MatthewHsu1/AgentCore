namespace AgentCore.Application.Knowledge;

/// <summary>
/// Carries the facets a search tool's argument named down to the search itself.
/// </summary>
internal static class ToolFacetScope
{
    private static readonly AsyncLocal<IReadOnlyDictionary<string, string>?> Named = new();

    /// <summary>Gets what a tool argument named on this flow, or <see langword="null"/> when none did.</summary>
    internal static IReadOnlyDictionary<string, string>? Current => Named.Value;

    /// <summary>Opens one set of named facets over this flow.</summary>
    /// <param name="facets">The facet key to stored value.</param>
    /// <returns>A handle that puts back what was open before.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="facets"/> is <see langword="null"/>.</exception>
    internal static IDisposable Open(IReadOnlyDictionary<string, string> facets)
    {
        ArgumentNullException.ThrowIfNull(facets);

        var before = Named.Value;
        Named.Value = facets;

        return new Handle(before);
    }

    private sealed class Handle(IReadOnlyDictionary<string, string>? before) : IDisposable
    {
        private bool _closed;

        public void Dispose()
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            Named.Value = before;
        }
    }
}
