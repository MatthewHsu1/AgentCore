using System.Runtime.CompilerServices;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Database.Postgres;

/// <summary>A theory that needs a live PostgreSQL, and skips itself when none is named.</summary>
/// <remarks>See <see cref="PostgresFactAttribute"/> for the variable it reads.</remarks>
public sealed class PostgresTheoryAttribute : TheoryAttribute
{
    /// <summary>Creates the attribute.</summary>
    /// <param name="sourceFilePath">Supplied by the compiler.</param>
    /// <param name="sourceLineNumber">Supplied by the compiler.</param>
    public PostgresTheoryAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = PostgresCluster.SkipReason;
        SkipUnless = nameof(PostgresCluster.IsConfigured);
        SkipType = typeof(PostgresCluster);
    }
}
