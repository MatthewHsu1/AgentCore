namespace AgentCore.Infrastructure.Tests.Database.Postgres;

/// <summary>The PostgreSQL cluster the integration tests run against, if the environment names one.</summary>
public static class PostgresCluster
{
    private const string Variable = "AGENTCORE_TEST_POSTGRES";

    /// <summary>Why a test skipped, when it did.</summary>
    public const string SkipReason = "Set AGENTCORE_TEST_POSTGRES to run.";

    /// <summary>The connection string, or null when the environment names no cluster.</summary>
    public static string? ConnectionString =>
        Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } value ? value : null;

    /// <summary>Whether a cluster was named.</summary>
    public static bool IsConfigured => ConnectionString is not null;
}
