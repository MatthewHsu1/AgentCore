namespace AgentCore.Domain.Knowledge;

/// <summary>Reads one value out of a neutral payload, walking a dotted path into nested maps.</summary>
/// <remarks>
/// Every path it walks comes from the document. This class knows no field name of its own.
/// </remarks>
public static class PayloadPath
{
    /// <summary>Walks a dotted path into a nested payload.</summary>
    /// <param name="payload">The payload to read, such as a card's <see cref="KnowledgeCard.Extras"/>.</param>
    /// <param name="path">The dotted path, or <see langword="null"/> for a role the document never mapped.</param>
    /// <returns>
    /// The value at the path, or <see langword="null"/> where the path names no value, leaves the
    /// map partway, or is itself empty. A missing path and a stored null are indistinguishable, which
    /// every caller so far treats the same way.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="payload"/> is <see langword="null"/>.</exception>
    public static object? Read(IReadOnlyDictionary<string, object?> payload, string? path)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (path is not { Length: > 0 })
        {
            return null;
        }

        object? current = payload;

        foreach (var part in path.Split('.'))
        {
            if (current is not IReadOnlyDictionary<string, object?> fields || !fields.TryGetValue(part, out current))
            {
                return null;
            }
        }

        return current;
    }
}
