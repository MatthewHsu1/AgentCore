using AgentCore.Application.Tools;

namespace AgentCore.Application.Tests.Tools.Fakes;

/// <summary>
/// An offline knowledge port that answers from a map, and can fail on demand.
/// </summary>
/// <remarks>
/// The built-in tools own the failure rule of section 8.7: an adapter that throws still produces an
/// error result. This fake throws when <see cref="Failure"/> is set, so the rule has something to
/// test against.
/// </remarks>
internal sealed class MapKnowledgePort : IKnowledgePort
{
    private readonly Dictionary<string, string> _documents = new(StringComparer.Ordinal);

    /// <summary>Gets or sets the failure every call raises, or null when the port answers.</summary>
    public Exception? Failure { get; set; }

    /// <summary>Gets every query this port was asked for, in call order.</summary>
    public List<string> Queries { get; } = [];

    /// <summary>Gets every limit this port was asked for, in call order.</summary>
    public List<int> Limits { get; } = [];

    /// <summary>Adds one document.</summary>
    public MapKnowledgePort With(string documentId, string text)
    {
        _documents[documentId] = text;
        return this;
    }

    public ValueTask<IReadOnlyList<KnowledgeChunk>> SearchAsync(
        string query,
        int limit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Queries.Add(query);
        Limits.Add(limit);

        if (Failure is { } failure)
        {
            throw failure;
        }

        var chunks = _documents
            .Where(entry => entry.Value.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(limit)
            .Select(entry => new KnowledgeChunk { DocumentId = entry.Key, Text = entry.Value, Score = 1.0 })
            .ToList();

        return ValueTask.FromResult<IReadOnlyList<KnowledgeChunk>>(chunks);
    }

    public ValueTask<KnowledgeDocument?> ReadAsync(string documentId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (Failure is { } failure)
        {
            throw failure;
        }

        return ValueTask.FromResult(_documents.TryGetValue(documentId, out var text)
            ? new KnowledgeDocument { DocumentId = documentId, Text = text }
            : null);
    }
}
