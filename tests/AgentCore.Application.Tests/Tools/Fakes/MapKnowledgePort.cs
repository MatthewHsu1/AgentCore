using System.Text.RegularExpressions;
using AgentCore.Application.Ports;
using AgentCore.Domain.Knowledge;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;

namespace AgentCore.Application.Tests.Tools.Fakes;

/// <summary>
/// An offline knowledge adapter that answers from a map, and can fail on demand.
/// </summary>
/// <remarks>
/// It answers both knowledge ports, exactly as the file store does, so one instance serves a test
/// that binds one port and a test that binds both. The built-in tools own the failure rule of
/// section 8.7: an adapter that throws still produces an error result. This fake throws when
/// <see cref="Failure"/> is set, so the rule has something to test against.
/// </remarks>
internal sealed class MapKnowledgePort : IKnowledgeRetrievalPort, IDocumentStorePort
{
    /// <summary>The caps the file store keeps, so the fake answers the same shape.</summary>
    private const int MaxListResults = 200;

    private const int MaxGrepMatches = 100;

    /// <summary>A root the glob matcher needs. No such directory is read.</summary>
    private static readonly string GlobRoot = Path.GetFullPath("map-knowledge-port");

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

    public ValueTask<DocumentListing> ListAsync(string? pattern = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (Failure is { } failure)
        {
            throw failure;
        }

        var ids = Matching(pattern);
        var truncated = ids.Count > MaxListResults;

        return ValueTask.FromResult(new DocumentListing
        {
            DocumentIds = truncated ? [.. ids.Take(MaxListResults)] : ids,
            Truncated = truncated,
        });
    }

    public ValueTask<GrepResult> GrepAsync(
        string pattern,
        string? glob = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (Failure is { } failure)
        {
            throw failure;
        }

        Regex regex = new(pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
        List<GrepMatch> matches = [];
        var truncated = false;

        foreach (var documentId in Matching(glob))
        {
            // The file store reads lines with File.ReadAllLines, which drops the empty line a final
            // line ending would otherwise add. The fake counts lines the same way.
            var text = _documents[documentId].ReplaceLineEndings("\n");
            var lines = (text.EndsWith('\n') ? text[..^1] : text).Split('\n');
            for (var line = 0; line < lines.Length && !truncated; line++)
            {
                if (!regex.IsMatch(lines[line]))
                {
                    continue;
                }

                truncated = matches.Count == MaxGrepMatches;
                if (!truncated)
                {
                    matches.Add(new GrepMatch
                    {
                        DocumentId = documentId,
                        LineNumber = line + 1,
                        Line = lines[line],
                    });
                }
            }

            if (truncated)
            {
                break;
            }
        }

        return ValueTask.FromResult(new GrepResult { Matches = matches, Truncated = truncated });
    }

    /// <summary>Names the documents a glob keeps, in the ordinal order the port promises.</summary>
    private List<string> Matching(string? glob)
    {
        List<string> ids = [.. _documents.Keys.OrderBy(id => id, StringComparer.Ordinal)];
        if (glob is not { Length: > 0 })
        {
            return ids;
        }

        Matcher matcher = new();
        matcher.AddInclude(glob);
        var kept = matcher
            .Execute(new InMemoryDirectoryInfo(GlobRoot, ids))
            .Files
            .Select(match => match.Path)
            .ToHashSet(StringComparer.Ordinal);

        return [.. ids.Where(kept.Contains)];
    }
}
