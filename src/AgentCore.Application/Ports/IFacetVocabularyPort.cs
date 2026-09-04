namespace AgentCore.Application.Ports;

/// <summary>
/// Reads the distinct values a store holds at one payload path — the vocabulary a
/// <c>vocabulary:</c> slot gates writes against and links mentions to.
/// </summary>
public interface IFacetVocabularyPort
{
    /// <summary>Reads the distinct values stored at one payload path.</summary>
    /// <param name="path">The payload path, already resolved from scope.template.</param>
    /// <param name="limit">The most values to return. A result of exactly this size is a truncation.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    ValueTask<IReadOnlyList<string>> ReadAsync(
        string path,
        int limit,
        CancellationToken cancellationToken = default);
}
