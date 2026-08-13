using AgentCore.Application.Configuration.Schema;
using AgentCore.Infrastructure.Knowledge;
using AgentCore.Infrastructure.Knowledge.FileStore;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Knowledge;

/// <summary>
/// The <c>filesystem</c> vendor of the knowledge seam.
/// </summary>
/// <remarks>
/// The composite routes a <c>kind</c> here and this adapter opens one
/// <see cref="FileSystemKnowledgeStore"/> over the root the document names. Both ports take that one
/// store, because the file store ranks and reads.
/// </remarks>
public sealed class FileSystemKnowledgeAdapterTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void ItServesTheFilesystemKindAndBothPorts()
    {
        FileSystemKnowledgeAdapter adapter = new();

        Assert.Equal("filesystem", adapter.Kind);
        Assert.True(adapter.CanServeSearch);
        Assert.True(adapter.CanServeDocuments);
    }

    [Fact]
    public async Task ItOpensOneStoreOverTheRootTheDocumentNames()
    {
        FileSystemKnowledgeAdapter adapter = new();
        KnowledgeProviderConfiguration entry = new() { Root = "./kb-of-this-test" };

        var search = await adapter.CreateSearchAsync(entry, null, Token);
        var documents = await adapter.CreateDocumentsAsync(entry, null, Token);

        // One tree, one store, and both ports on it.
        Assert.Same(search, documents);
        Assert.Equal(Path.GetFullPath("./kb-of-this-test"), Assert.IsType<FileSystemKnowledgeStore>(search).Root);
    }
}
