using Xunit;

namespace AgentCore.Evals;

/// <summary>
/// Checks that <see cref="EvalHarness.DatasetSkipReason"/> names every missing variable, not just
/// the first one it finds.
/// </summary>
/// <remarks>
/// A deployer who sets <c>AGENTCORE_EVAL_DATASET</c> and forgets <c>AGENTCORE_TEST_QDRANT</c>, or the
/// other way around, has to be told which one is still missing. A reason that only ever names the
/// first gate it checks is the silent-skip failure mode this suite exists to rule out.
/// </remarks>
public sealed class EvalHarnessTests
{
    [Fact]
    public void DatasetSkipReason_NothingSet_NamesAllThreeVariables()
    {
        using var scope = EnvironmentScope.ClearAll();

        var reason = EvalHarness.DatasetSkipReason;

        Assert.NotNull(reason);
        Assert.Contains(GoldenSet.DatasetVariable, reason, StringComparison.Ordinal);
        Assert.Contains(EvalHarness.ConfigurationVariable, reason, StringComparison.Ordinal);
        Assert.Contains(EvalHarness.QdrantVariable, reason, StringComparison.Ordinal);
        Assert.False(EvalHarness.DatasetIsConfigured);
    }

    [Fact]
    public void DatasetSkipReason_OnlyQdrantSet_NamesTheDatasetAndTheConfiguration()
    {
        using var scope = EnvironmentScope.ClearAll();
        Environment.SetEnvironmentVariable(EvalHarness.QdrantVariable, "localhost:6334");

        var reason = EvalHarness.DatasetSkipReason;

        Assert.NotNull(reason);
        Assert.Contains(GoldenSet.DatasetVariable, reason, StringComparison.Ordinal);
        Assert.Contains(EvalHarness.ConfigurationVariable, reason, StringComparison.Ordinal);
        Assert.DoesNotContain(EvalHarness.QdrantVariable, reason, StringComparison.Ordinal);
        Assert.False(EvalHarness.DatasetIsConfigured);
    }

    [Fact]
    public void DatasetSkipReason_DatasetAndConfigurationSetButNotQdrant_NamesOnlyQdrant()
    {
        using var scope = EnvironmentScope.ClearAll();
        Environment.SetEnvironmentVariable(GoldenSet.DatasetVariable, "golden.jsonl");
        Environment.SetEnvironmentVariable(EvalHarness.ConfigurationVariable, "agent.yaml");

        var reason = EvalHarness.DatasetSkipReason;

        Assert.NotNull(reason);
        Assert.DoesNotContain(GoldenSet.DatasetVariable, reason, StringComparison.Ordinal);
        Assert.DoesNotContain(EvalHarness.ConfigurationVariable, reason, StringComparison.Ordinal);
        Assert.Contains(EvalHarness.QdrantVariable, reason, StringComparison.Ordinal);
        Assert.False(EvalHarness.DatasetIsConfigured);
    }

    [Fact]
    public void DatasetSkipReason_AllThreeSet_IsNull()
    {
        using var scope = EnvironmentScope.ClearAll();
        Environment.SetEnvironmentVariable(GoldenSet.DatasetVariable, "golden.jsonl");
        Environment.SetEnvironmentVariable(EvalHarness.ConfigurationVariable, "agent.yaml");
        Environment.SetEnvironmentVariable(EvalHarness.QdrantVariable, "localhost:6334");

        Assert.Null(EvalHarness.DatasetSkipReason);
        Assert.True(EvalHarness.DatasetIsConfigured);
    }

    /// <summary>Saves the three variables this suite touches, and restores them on dispose.</summary>
    /// <remarks>
    /// This assembly disables parallelisation (see <c>AssemblyInfo.cs</c>), so a test that changes
    /// process-wide environment variables cannot race another test reading the same three names.
    /// </remarks>
    private sealed class EnvironmentScope : IDisposable
    {
        private readonly string? _dataset = Environment.GetEnvironmentVariable(GoldenSet.DatasetVariable);
        private readonly string? _configuration = Environment.GetEnvironmentVariable(EvalHarness.ConfigurationVariable);
        private readonly string? _qdrant = Environment.GetEnvironmentVariable(EvalHarness.QdrantVariable);

        public static EnvironmentScope ClearAll()
        {
            EnvironmentScope scope = new();
            Environment.SetEnvironmentVariable(GoldenSet.DatasetVariable, null);
            Environment.SetEnvironmentVariable(EvalHarness.ConfigurationVariable, null);
            Environment.SetEnvironmentVariable(EvalHarness.QdrantVariable, null);
            return scope;
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(GoldenSet.DatasetVariable, _dataset);
            Environment.SetEnvironmentVariable(EvalHarness.ConfigurationVariable, _configuration);
            Environment.SetEnvironmentVariable(EvalHarness.QdrantVariable, _qdrant);
        }
    }
}
