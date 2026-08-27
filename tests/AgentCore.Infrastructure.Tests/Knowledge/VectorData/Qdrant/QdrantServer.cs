using Qdrant.Client;

namespace AgentCore.Infrastructure.Tests.Knowledge.VectorData.Qdrant;

/// <summary>The Qdrant the query tests run against, if the environment names one.</summary>
/// <remarks>
/// <c>AGENTCORE_TEST_QDRANT</c> names host and gRPC port, for example <c>localhost:6334</c>. The
/// tests create and drop their own collections, so the server must be a scratch one.
/// <b>The skip is silent:</b> a green local run on a machine with no Qdrant means these tests did
/// not run at all. CI always sets the variable.
/// </remarks>
public static class QdrantServer
{
    private const string Variable = "AGENTCORE_TEST_QDRANT";

    /// <summary>Why a test skipped, when it did.</summary>
    public const string SkipReason = "Set AGENTCORE_TEST_QDRANT (host:port) to run.";

    /// <summary>The endpoint, or null when the environment names none.</summary>
    public static string? Endpoint =>
        Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } value ? value : null;

    /// <summary>Whether a server was named.</summary>
    public static bool IsConfigured => Endpoint is not null;

    /// <summary>Opens a client on the named server.</summary>
    /// <returns>The client. The caller disposes it.</returns>
    /// <exception cref="InvalidOperationException">No server was named.</exception>
    public static QdrantClient CreateClient()
    {
        var endpoint = Endpoint ?? throw new InvalidOperationException(SkipReason);
        var parts = endpoint.Split(':', 2);

        // The timeout is per call and the connector never sets one. A schema call on a slow disk
        // can take 20 s; a query takes single-digit milliseconds.
        return new QdrantClient(parts[0], int.Parse(parts[1]), grpcTimeout: TimeSpan.FromSeconds(30));
    }
}
