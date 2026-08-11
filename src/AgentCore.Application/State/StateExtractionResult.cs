namespace AgentCore.Application.State;

/// <summary>
/// What one extractor call did to the state.
/// </summary>
/// <remarks>
/// Section 8.7: the extractor never drops a call. A reply that does not deserialize leaves every
/// slot unchanged, and the turn loop logs it once and continues.
/// </remarks>
/// <param name="Deserialized">Whether the reply parsed into the object the schema describes.</param>
/// <param name="Filled">The number of slots the reply filled.</param>
/// <param name="LeftNull">The number of slots the reply answered with <c>null</c>.</param>
/// <param name="Rejected">The number of answers that did not coerce to the declared type.</param>
/// <param name="Failure">The reason the reply did not parse, or <see langword="null"/>.</param>
public sealed record StateExtractionResult(
    bool Deserialized,
    int Filled,
    int LeftNull,
    int Rejected,
    string? Failure)
{
    /// <summary>Gets the result of a call that produced nothing usable.</summary>
    /// <param name="failure">The reason.</param>
    /// <returns>A result that changed no slot.</returns>
    public static StateExtractionResult Failed(string failure) => new(false, 0, 0, 0, failure);
}
