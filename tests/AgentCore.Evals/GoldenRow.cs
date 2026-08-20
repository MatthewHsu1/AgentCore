using System.Text.Json.Serialization;

namespace AgentCore.Evals;

/// <summary>
/// One row of a golden set.
/// </summary>
/// <remarks>
/// <para>
/// The row holds what a person can check by hand: the question, the file that answers it, and the
/// fault codes a correct answer states. It holds no expected reply text. Two correct replies word the
/// same fact differently, so a string comparison over the reply would call one of them wrong.
/// </para>
/// <para>
/// The row names a document and never a passage. A knowledge base that re-chunks the same text
/// answers the same question, and a score that moved would report a failure that is not there.
/// </para>
/// </remarks>
public sealed record GoldenRow
{
    /// <summary>Gets the stable id of this row. It names the scenario in the report.</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>Gets what the caller asks.</summary>
    [JsonPropertyName("query")]
    public required string Query { get; init; }

    /// <summary>Gets the document ids that answer the query.</summary>
    [JsonPropertyName("expectedDocumentIds")]
    public required IReadOnlyList<string> ExpectedDocumentIds { get; init; }

    /// <summary>Gets the fault codes a correct reply states. It is empty when the row names none.</summary>
    [JsonPropertyName("expectedFaultCodes")]
    public IReadOnlyList<string> ExpectedFaultCodes { get; init; } = [];

    /// <summary>Gets the free labels that slice the report, such as a machine family.</summary>
    [JsonPropertyName("tags")]
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Names this row in a test display and in the report.</summary>
    public override string ToString() => Id;
}
