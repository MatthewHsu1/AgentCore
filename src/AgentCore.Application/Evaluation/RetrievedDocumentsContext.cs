using Microsoft.Extensions.AI.Evaluation;

namespace AgentCore.Application.Evaluation;

/// <summary>
/// The documents one search returned, and the documents the golden row expects.
/// </summary>
/// <remarks>
/// <para>
/// The measurement is the file and not the passage, so both sides hold
/// <see cref="AgentCore.Domain.Knowledge.KnowledgeChunk.DocumentId"/> values. A chunk boundary belongs
/// to the store: a knowledge base that re-chunks the same text answers the same question, and a score
/// that moved would report a failure that is not there.
/// </para>
/// <para>
/// The context carries what the row expects, which a person wrote, and what the search returned, which
/// the store decided. <see cref="DocumentRecallEvaluator"/> is the only reader of it.
/// </para>
/// </remarks>
public sealed class RetrievedDocumentsContext : EvaluationContext
{
    /// <summary>The name this context carries.</summary>
    public const string RetrievedDocumentsContextName = "Retrieved Documents";

    /// <summary>Builds a context from the expected documents and the returned documents.</summary>
    /// <param name="expected">The document ids that answer the query.</param>
    /// <param name="retrieved">The document ids the search returned, best first.</param>
    /// <exception cref="ArgumentNullException">Either list is <see langword="null"/>.</exception>
    public RetrievedDocumentsContext(IEnumerable<string> expected, IEnumerable<string> retrieved)
        : this(Materialize(expected, nameof(expected)), Materialize(retrieved, nameof(retrieved)))
    {
    }

    private RetrievedDocumentsContext(string[] expected, string[] retrieved)
        : base(RetrievedDocumentsContextName, Describe(expected, retrieved))
    {
        Expected = expected;
        Retrieved = retrieved;
    }

    /// <summary>Gets the document ids that answer the query.</summary>
    public IReadOnlyList<string> Expected { get; }

    /// <summary>Gets the document ids the search returned, best first.</summary>
    public IReadOnlyList<string> Retrieved { get; }

    private static string Describe(string[] expected, string[] retrieved)
        => $"expected: {Join(expected)}; retrieved: {Join(retrieved)}";

    private static string Join(string[] ids) => ids.Length == 0 ? "none" : string.Join(", ", ids);

    private static string[] Materialize(IEnumerable<string> ids, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(ids, parameterName);
        return [.. ids];
    }
}
