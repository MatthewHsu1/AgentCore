using AgentCore.Application.Ports;
using Xunit;

namespace AgentCore.Evals;

/// <summary>
/// Checks that every row names a document the knowledge base holds.
/// </summary>
/// <remarks>
/// <para>
/// A row that names a document the store does not hold scores a recall of 0, and the report then
/// reads as a retrieval failure. It is not one. It is a stale row, and the usual cause is a renamed
/// or a deleted file.
/// </para>
/// <para>
/// The check reads the document port and never the ranking port, so it separates "the file is gone"
/// from "the search did not rank it". It calls no model and reads no key, so it costs a second. For a
/// deployment it is the most valuable test in the suite, because a rename is the most common way a
/// golden set goes stale.
/// </para>
/// </remarks>
public sealed class GoldenSetIntegrityTests(DatasetFixture fixture) : IClassFixture<DatasetFixture>
{
    [Theory]
    [MemberData(nameof(GoldenSet.Fixture), MemberType = typeof(GoldenSet))]
    public async Task FixtureRow_NamesOnlyDocumentsTheFixtureKnowledgeBaseHolds(GoldenRow row)
    {
        // Arrange
        var (_, documents) = EvalHarness.OpenFixtureKnowledge();

        // Act
        var missing = await MissingDocumentsAsync(row, documents);

        // Assert
        Assert.Empty(missing);
    }

    // SkipTestWithoutData: a repository that names no dataset feeds no row, and that is the reported
    // state of this repository rather than a failure.
    [Theory(SkipTestWithoutData = true)]
    [MemberData(nameof(GoldenSet.Dataset), MemberType = typeof(GoldenSet))]
    public async Task DatasetRow_NamesOnlyDocumentsTheKnowledgeBaseHolds(GoldenRow row)
    {
        Assert.SkipUnless(
            fixture.Harness is not null,
            $"{GoldenSet.DatasetVariable} and {EvalHarness.ConfigurationVariable} name no golden set.");

        // Arrange, Act
        var missing = await MissingDocumentsAsync(row, fixture.Harness!.Documents);

        // Assert
        Assert.True(
            missing.Count == 0,
            $"row {row.Id} names {string.Join(", ", missing)}, and the knowledge base holds no such "
                + "document. Either the file moved, or the row is stale.");
    }

    private static async Task<IReadOnlyList<string>> MissingDocumentsAsync(
        GoldenRow row,
        IDocumentStorePort documents)
    {
        List<string> missing = [];
        foreach (var id in row.ExpectedDocumentIds)
        {
            var document = await documents.ReadAsync(id, TestContext.Current.CancellationToken);
            if (document is null)
            {
                missing.Add(id);
            }
        }

        return missing;
    }
}
