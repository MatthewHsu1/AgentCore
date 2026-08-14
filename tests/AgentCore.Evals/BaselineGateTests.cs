using Xunit;

namespace AgentCore.Evals;

/// <summary>
/// The gate that fails a merge on a score drop.
/// </summary>
/// <remarks>
/// <para>
/// The gate reads what the eval suites already wrote to the disk store, so it has to run after them.
/// A test run gives no order, so the gate carries the <c>Gate</c> category and CI runs two commands:
/// </para>
/// <code>
/// dotnet test tests/AgentCore.Evals --filter "Category!=Gate"
/// dotnet test tests/AgentCore.Evals --filter "Category=Gate"
/// </code>
/// <para>
/// A drop larger than the recorded tolerance fails. A rise never fails. The gate writes what it
/// measured to <c>eval-results/measured.json</c>, so a person who accepts a change copies the numbers
/// into the baseline by hand.
/// </para>
/// </remarks>
[Trait("Category", "Gate")]
public sealed class BaselineGateTests
{
    private const string MeasuredPath = "eval-results/measured.json";

    [Fact]
    public async Task FixtureExecution_HoldsItsRecordedScores()
    {
        // Arrange
        var baseline = ScoreBaseline.Load(ScoreBaseline.FixtureBaselinePath);

        // Act
        var measured = await ScoreBaseline.MeasureAsync(
            EvalHarness.StorageRoot,
            EvalHarness.FixtureExecution,
            TestContext.Current.CancellationToken);

        // Assert
        AssertNoDrop(baseline, measured, EvalHarness.FixtureExecution);
    }

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
