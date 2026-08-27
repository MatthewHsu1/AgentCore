using System.Runtime.CompilerServices;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Knowledge.VectorData.Qdrant;

/// <summary>A test that needs a live Qdrant, and skips itself when none is named.</summary>
/// <remarks>See <see cref="QdrantServer"/> for the variable it reads.</remarks>
public sealed class QdrantFactAttribute : FactAttribute
{
    /// <summary>Creates the attribute.</summary>
    /// <param name="sourceFilePath">Supplied by the compiler.</param>
    /// <param name="sourceLineNumber">Supplied by the compiler.</param>
    public QdrantFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = QdrantServer.SkipReason;
        SkipUnless = nameof(QdrantServer.IsConfigured);
        SkipType = typeof(QdrantServer);
    }
}
