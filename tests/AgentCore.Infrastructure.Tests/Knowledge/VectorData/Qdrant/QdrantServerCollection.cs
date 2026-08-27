using Xunit;

namespace AgentCore.Infrastructure.Tests.Knowledge.VectorData.Qdrant;

/// <summary>
/// The collection of every class that creates a collection against the shared scratch Qdrant.
/// </summary>
/// <remarks>
/// A test run gives each class a collection of its own and runs collections in parallel by default.
/// Three classes creating collections in the same millisecond against an HDD-backed container has
/// already produced a real flake: <c>CreatePayloadIndexAsync</c> exceeded the client's 30 s deadline
/// and took down an unrelated class with it. Naming this collection puts every Qdrant-backed test
/// class in one queue, so no two ever race the same server at once. It fixes no order among them.
/// </remarks>
[CollectionDefinition(Name)]
public sealed class QdrantServerCollection
{
    /// <summary>The name every class that talks to the shared scratch Qdrant carries.</summary>
    public const string Name = "QdrantServer";
}
