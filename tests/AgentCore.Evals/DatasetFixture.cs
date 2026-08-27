using Xunit;

namespace AgentCore.Evals;

/// <summary>
/// Builds the golden-set harness once for a test class.
/// </summary>
/// <remarks>
/// Opening the knowledge base costs a socket, a secret read, and an embedding call, and every row of
/// a suite reads the same one. The fixture builds it once and holds it for the class.
/// </remarks>
public sealed class DatasetFixture : IAsyncLifetime
{
    /// <summary>Gets the harness, or <see langword="null"/> when this run names no golden set.</summary>
    public DatasetHarness? Harness { get; private set; }

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        if (EvalHarness.DatasetIsConfigured)
        {
            Harness = await DatasetHarness.CreateAsync(TestContext.Current.CancellationToken);
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Harness?.Dispose();
        return ValueTask.CompletedTask;
    }
}
