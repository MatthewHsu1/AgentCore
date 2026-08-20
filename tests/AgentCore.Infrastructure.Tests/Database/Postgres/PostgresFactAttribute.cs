using System.Runtime.CompilerServices;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Database.Postgres;

/// <summary>A test that needs a live PostgreSQL, and skips itself when none is named.</summary>
/// <remarks>
/// <c>AGENTCORE_TEST_POSTGRES</c> names the cluster, for example
/// <c>Host=localhost;Port=55432;Username=postgres;Password=pw</c>. The role it names must be allowed
/// to create databases and roles. Steps 7 to 10 of the build add more tests behind the same gate.
/// </remarks>
public sealed class PostgresFactAttribute : FactAttribute
{
    /// <summary>Creates the attribute.</summary>
    /// <param name="sourceFilePath">Supplied by the compiler.</param>
    /// <param name="sourceLineNumber">Supplied by the compiler.</param>
    public PostgresFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = PostgresCluster.SkipReason;
        SkipUnless = nameof(PostgresCluster.IsConfigured);
        SkipType = typeof(PostgresCluster);
    }
}
