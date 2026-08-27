using Xunit;

namespace AgentCore.Evals;

/// <summary>
/// The gate that fails a merge on a score drop.
/// </summary>
/// <remarks>
/// <para>
/// The gate reads what the eval suites already wrote to the disk store, so it has to run after them.
/// That order is now structural: this class sits alone in <see cref="EvalGateCollection"/>, and
/// <see cref="GateLastCollectionOrderer"/> runs that collection after every other one. So a plain
/// <c>dotnet test tests/AgentCore.Evals</c>, with no filter, passes from an empty results directory.
/// </para>
/// <para>
/// The <c>Gate</c> category stays, because CI still splits the run in two:
/// </para>
/// <code>
/// dotnet test tests/AgentCore.Evals --filter "Category!=Gate"
/// dotnet test tests/AgentCore.Evals --filter "Category=Gate"
/// </code>
/// <para>
/// The split is what separates a broken harness from a dropped score in the CI log, and it lets the
/// two halves carry different secrets. It is no longer what supplies the order. Run the second command
/// on its own against an empty directory and it still fails, correctly: nothing wrote the results it
/// measures.
/// </para>
/// <para>
/// A drop larger than the recorded tolerance fails. A rise never fails. The gate writes what it
/// measured to <c>eval-results/measured.json</c>, so a person who accepts a change copies the numbers
/// into the baseline by hand.
/// </para>
/// <para>
/// There is no fixture-execution gate. The synthetic knowledge base and the retrieval self-test that
/// wrote to the "fixture" execution were both deleted with the old filesystem store (Ruling 9): the
/// store that replaced it needs a real embedding model for its dense leg, which the offline fixture
/// suite has no key for, so nothing writes a "Document Recall" score outside a real, key-gated golden
/// set. A gate that always skips is worse than no gate, so this class reads only the "golden-set"
/// execution below.
/// </para>
/// </remarks>
[Trait("Category", "Gate")]
[Collection(EvalGateCollection.Name)]
public sealed class BaselineGateTests
{
    private const string MeasuredPath = "eval-results/measured.json";

    [Fact]
    public async Task DatasetExecution_HoldsItsRecordedScores()
    {
        Assert.SkipUnless(
            ScoreBaseline.DatasetBaselinePath is not null,
            $"{ScoreBaseline.BaselineVariable} names no baseline, so there is nothing to gate against.");

        // Arrange
        var baseline = ScoreBaseline.Load(ScoreBaseline.DatasetBaselinePath!);

        // Act
        var measured = await ScoreBaseline.MeasureAsync(
            EvalHarness.StorageRoot,
            EvalHarness.DatasetExecution,
            TestContext.Current.CancellationToken);

        // Assert
        AssertNoDrop(baseline, measured, EvalHarness.DatasetExecution);
    }

    private static void AssertNoDrop(
        ScoreBaseline baseline,
        IReadOnlyDictionary<string, double> measured,
        string executionName)
    {
        ScoreBaseline.WriteMeasured(MeasuredPath, measured, baseline.Tolerance);

        Assert.True(
            measured.Count > 0,
            $"the store holds no result for execution '{executionName}'. Run the eval suites first.");

        List<string> drops = [];
        foreach (var entry in baseline.Metrics)
        {
            if (!measured.TryGetValue(entry.Key, out var score))
            {
                drops.Add($"'{entry.Key}' is in the baseline and no row measured it.");
                continue;
            }

            var floor = entry.Value - baseline.Tolerance;
            if (score < floor)
            {
                drops.Add(
                    $"'{entry.Key}' scored {score} against a baseline of {entry.Value} "
                        + $"and a floor of {Math.Round(floor, 4)}.");
            }
        }

        Assert.True(
            drops.Count == 0,
            $"execution '{executionName}' dropped. {string.Join(" ", drops)} "
                + $"The measured scores are in {MeasuredPath}.");
    }
}
