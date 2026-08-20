using System.Globalization;
using System.Text.RegularExpressions;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.Domain.Knowledge;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;

namespace AgentCore.Infrastructure.Knowledge;

/// <summary>
/// A knowledge base that is a directory tree of text files.
/// </summary>
/// <remarks>
/// <para>
/// <c>providers.knowledge.root</c> names the tree and the default is <c>./kb</c>. The tree is its
/// own Git repository, so a document id is the path of the file below the root, written with forward
/// slashes: <c>policies/shipping.md</c> reads back exactly as the search result wrote it.
/// </para>
/// <para>
/// The ranking is word overlap over the paragraphs of each file, and it is deterministic. It answers
/// without a vector store and without an embedding call, which is what a first deployment and every
/// test need. <c>providers.knowledge.store</c> names the vector store, and no adapter binds it yet.
/// </para>
/// <para>
/// A root that is not there reads as an empty knowledge base rather than a failure, because the
/// built-in tool turns any failure into an error result and an empty answer says the same thing more
/// plainly.
/// </para>
/// <para>
/// This one class answers both knowledge ports. It is the document store of section 7, and it also
/// ranks until the Zilliz connector binds <see cref="IKnowledgeRetrievalPort"/>. On that day the
/// host binds the two ports to two adapters and this class keeps only the reading half.
/// </para>
/// </remarks>
public sealed class FileSystemKnowledgeStore : IKnowledgeRetrievalPort, IDocumentStorePort
{
    /// <summary>The largest number of ids one listing names.</summary>
    internal const int MaxListResults = 200;

    /// <summary>The largest number of lines one grep returns.</summary>
    internal const int MaxGrepMatches = 100;

    /// <summary>The file extensions the tree holds.</summary>
    private static readonly string[] Extensions = [".md", ".markdown", ".txt"];

    /// <summary>How long one line may take to match before the pattern gives up.</summary>
    private static readonly TimeSpan GrepTimeout = TimeSpan.FromSeconds(1);

    /// <summary>The characters that end one word of a query or a passage.</summary>
    private static readonly char[] WordBreaks =
        [' ', '\t', '\r', '\n', '.', ',', ';', ':', '!', '?', '(', ')', '[', ']', '{', '}', '"', '\'', '/', '\\'];

    /// <summary>Creates a store over one root.</summary>
    /// <param name="root">The root of the knowledge tree.</param>
    public FileSystemKnowledgeStore(string root)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        Root = Path.GetFullPath(root);
    }

    /// <summary>Creates a store over the root the <c>providers:</c> section names.</summary>
    /// <param name="knowledge">The knowledge provider, or <see langword="null"/> for the default root.</param>
    public FileSystemKnowledgeStore(KnowledgeProviderConfiguration? knowledge)
        : this(knowledge?.Root is { Length: > 0 } root ? root : KnowledgeProviderConfiguration.DefaultRoot)
    {
    }

    /// <summary>Gets the root of the knowledge tree, as an absolute path.</summary>
    public string Root { get; }

    /// <summary>Ranks the passages that answer one query.</summary>
    /// <param name="query">What the model is looking for.</param>
    /// <param name="limit">The largest number of passages to return.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>The passages, best first.</returns>
    public async ValueTask<IReadOnlyList<KnowledgeChunk>> SearchAsync(
        string query,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var terms = Words(query);
        if (terms.Count == 0 || limit <= 0 || !Directory.Exists(Root))
        {
            return [];
        }

        List<KnowledgeChunk> found = [];
        foreach (var path in Files())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var documentId = DocumentId(path);
            var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);

            foreach (var passage in Passages(text))
            {
                var score = Score(passage, terms);
                if (score > 0)
                {
                    found.Add(new KnowledgeChunk { DocumentId = documentId, Text = passage, Score = score });
                }
            }
        }

        return
        [
            .. found
                .OrderByDescending(chunk => chunk.Score)
                .ThenBy(chunk => chunk.DocumentId, StringComparer.Ordinal)
                .Take(limit),
        ];
    }

    /// <summary>Reads one whole document.</summary>
    /// <param name="documentId">The id a search result named.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The document, or <see langword="null"/> when the tree holds no such id.</returns>
    public async ValueTask<KnowledgeDocument?> ReadAsync(
        string documentId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documentId);

        if (!TryResolvePath(documentId, out var path))
        {
            return null;
        }

        var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return new KnowledgeDocument { DocumentId = documentId, Text = text };
    }

    /// <summary>Names the documents the tree holds.</summary>
    /// <param name="pattern">A glob over document ids, or <see langword="null"/> for every document.</param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>The ids, in ordinal order, capped at <see cref="MaxListResults"/>.</returns>
    public ValueTask<DocumentListing> ListAsync(
        string? pattern = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var ids = MatchingIds(pattern);
        var truncated = ids.Count > MaxListResults;

        return ValueTask.FromResult(new DocumentListing
        {
            DocumentIds = truncated ? [.. ids.Take(MaxListResults)] : ids,
            Truncated = truncated,
        });
    }

    /// <summary>Finds the lines that match one pattern.</summary>
    /// <param name="pattern">The regular expression each line is matched against.</param>
    /// <param name="glob">A glob over document ids, or <see langword="null"/> for every document.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>The matches, in ordinal order, capped at <see cref="MaxGrepMatches"/>.</returns>
    public async ValueTask<GrepResult> GrepAsync(
        string pattern,
        string? glob = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pattern);

        // A pattern the model wrote badly, and a pattern that runs past the timeout, both throw. The
        // built-in tool turns either one into an error result, so the store says nothing about them.
        Regex regex = new(pattern, RegexOptions.None, GrepTimeout);
        List<GrepMatch> matches = [];
        var truncated = false;

        foreach (var documentId in MatchingIds(glob))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!TryResolvePath(documentId, out var path))
            {
                continue;
            }

            var lines = await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false);
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

        return new GrepResult { Matches = matches, Truncated = truncated };
    }

    /// <summary>Walks every file of the tree, in a stable order.</summary>
    private IEnumerable<string> Files()
        => Directory
            .EnumerateFiles(Root, "*", SearchOption.AllDirectories)
            .Where(path => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.Ordinal);

    /// <summary>
    /// Names the documents a glob keeps, in ordinal order, and never one outside the tree.
    /// </summary>
    /// <remarks>
    /// The glob runs over the ids the tree already holds, so a pattern that climbs out of the root
    /// with <c>../</c> matches nothing rather than reads a file the knowledge base does not own.
    /// </remarks>
    private List<string> MatchingIds(string? glob)
    {
        if (!Directory.Exists(Root))
        {
            return [];
        }

        List<string> ids = [.. Files().Select(DocumentId).OrderBy(id => id, StringComparer.Ordinal)];
        if (glob is not { Length: > 0 })
        {
            return ids;
        }

        Matcher matcher = new();
        matcher.AddInclude(glob);
        var kept = matcher
            .Execute(new InMemoryDirectoryInfo(Root, ids))
            .Files
            .Select(match => match.Path)
            .ToHashSet(StringComparer.Ordinal);

        return [.. ids.Where(kept.Contains)];
    }

    /// <summary>Turns a file path into the id a search result carries.</summary>
    private string DocumentId(string path)
        => Path.GetRelativePath(Root, path).Replace(Path.DirectorySeparatorChar, '/');

    /// <summary>Turns an id back into a file path, and never leaves the root.</summary>
    private bool TryResolvePath(string documentId, out string path)
    {
        path = string.Empty;

        if (documentId.Length == 0 || Path.IsPathRooted(documentId))
        {
            return false;
        }

        var candidate = Path.GetFullPath(Path.Combine(Root, documentId));
        if (!candidate.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || !File.Exists(candidate))
        {
            return false;
        }

        path = candidate;
        return true;
    }

    /// <summary>Splits one document into the passages a chunk quotes.</summary>
    private static IEnumerable<string> Passages(string text)
    {
        foreach (var passage in text.ReplaceLineEndings("\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = passage.Trim();
            if (trimmed.Length > 0)
            {
                yield return trimmed;
            }
        }
    }

    /// <summary>Splits text into the words the ranking compares.</summary>
    private static List<string> Words(string text)
    {
        List<string> words = [];
        foreach (var word in text.Split(WordBreaks, StringSplitOptions.RemoveEmptyEntries))
        {
            var lowered = word.ToLowerInvariant().Trim('-', '*', '#');
            if (lowered.Length > 0 && !words.Contains(lowered, StringComparer.Ordinal))
            {
                words.Add(lowered);
            }
        }

        return words;
    }

    /// <summary>Scores one passage as the share of query words it holds.</summary>
    private static double Score(string passage, List<string> terms)
    {
        var words = Words(passage);
        var hits = terms.Count(term => words.Contains(term, StringComparer.Ordinal));

        return hits == 0 ? 0 : Math.Round(hits / (double)terms.Count, 4);
    }
}
