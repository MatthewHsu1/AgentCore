using Xunit;

namespace AgentCore.Evals;

/// <summary>
/// Builds the golden-set harness once for a test class.
/// </summary>
/// <remarks>
/// Opening the knowledge base and the chat clients costs a socket and a secret read, and every row of
/// a suite reads the same two. The fixture builds them once and holds them for the class.
/// </remarks>
public sealed class DatasetFixture : IAsyncLifetime
{
    /// <summary>Gets the harness, or <see langword="null"/> when this repository names no dataset.</summary>
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
