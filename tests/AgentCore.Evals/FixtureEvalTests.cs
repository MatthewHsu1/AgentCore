using Xunit;

namespace AgentCore.Evals;

/// <summary>
/// Checks the shape of the synthetic golden set that travels with this repository.
/// </summary>
/// <remarks>
/// This is a format check only, and it always runs. The retrieval self-test that used to run here
/// read a five-file knowledge base through the old filesystem store; that store is gone (Ruling 9),
/// and the store that replaced it needs a real embedding model for its dense leg, which this offline
/// suite has no key for. So this class proves the row format still parses and never scores a search.
/// </remarks>
public sealed class FixtureEvalTests
{
    [Fact]
    public void Fixture_HoldsEnoughRowsToExerciseTheFormat()
    {
        // A row with one expected document and a row with two exercise both branches of the metric.

        // Arrange, Act
        var rows = GoldenSet.Load(GoldenSet.FixturePath);

        // Assert
        Assert.Contains(rows, row => row.ExpectedDocumentIds.Count == 1);
        Assert.Contains(rows, row => row.ExpectedDocumentIds.Count > 1);
        Assert.Contains(rows, row => row.ExpectedFaultCodes.Count > 0);
    }
}
