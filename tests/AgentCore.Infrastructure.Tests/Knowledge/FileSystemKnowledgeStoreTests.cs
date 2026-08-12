using AgentCore.Application.Configuration.Schema;
using AgentCore.Infrastructure.Knowledge;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Knowledge;

/// <summary>
/// The knowledge adapter that reads a directory tree.
/// </summary>
/// <remarks>
/// <c>providers.knowledge.root</c> names the tree, and the default is <c>./kb</c>. The tree is the
/// same one the vector store indexes later, so a deployment that has no vector store still answers.
/// </remarks>
public sealed class FileSystemKnowledgeStoreTests
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "kb-example");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void ItTakesTheRootTheProvidersSectionNames()
    {
        FileSystemKnowledgeStore store = new(new KnowledgeProviderConfiguration { Store = "zilliz", Root = "./kb" });

        Assert.Equal(Path.GetFullPath("./kb"), store.Root);
    }

    [Fact]
    public void WithNoProviderSection_ItTakesTheDefaultRoot()
    {
        FileSystemKnowledgeStore store = new(knowledge: null);

        Assert.Equal(Path.GetFullPath(KnowledgeProviderConfiguration.DefaultRoot), store.Root);
    }

    [Fact]
    public async Task SearchRanksThePassagesThatHoldTheWords()
    {
        FileSystemKnowledgeStore store = new(Root);

        var chunks = await store.SearchAsync("refund card", 5, Token);

        Assert.NotEmpty(chunks);
        Assert.Equal("returns.md", chunks[0].DocumentId);
        Assert.Contains("refund", chunks[0].Text, StringComparison.OrdinalIgnoreCase);
        Assert.True(chunks[0].Score > 0);
    }

    [Fact]
    public async Task SearchReachesEveryDirectoryUnderTheRoot()
    {
        FileSystemKnowledgeStore store = new(Root);

        var chunks = await store.SearchAsync("pallet", 5, Token);

        Assert.Equal("policies/shipping.md", Assert.Single(chunks).DocumentId);
    }

    [Fact]
    public async Task SearchKeepsToTheLimit()
    {
        FileSystemKnowledgeStore store = new(Root);

        var chunks = await store.SearchAsync("treadmill", 1, Token);

        Assert.Single(chunks);
    }

    [Fact]
    public async Task SearchThatMatchesNothing_ReturnsNothing()
    {
        FileSystemKnowledgeStore store = new(Root);

        Assert.Empty(await store.SearchAsync("aardvark", 5, Token));
    }

    [Fact]
    public async Task SearchOverARootThatIsNotThere_ReturnsNothing()
    {
        FileSystemKnowledgeStore store = new(Path.Combine(AppContext.BaseDirectory, "kb-missing"));

        Assert.Empty(await store.SearchAsync("refund", 5, Token));
    }

    [Fact]
    public async Task ReadReturnsTheWholeDocument()
    {
        FileSystemKnowledgeStore store = new(Root);

        var document = await store.ReadAsync("returns.md", Token);

        Assert.NotNull(document);
        Assert.Equal("returns.md", document.DocumentId);
        Assert.Contains("thirty days", document.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadOfADocumentThatIsNotThere_ReturnsNull()
    {
        FileSystemKnowledgeStore store = new(Root);

        Assert.Null(await store.ReadAsync("nothing.md", Token));
    }

    [Theory]
    [InlineData("../../../etc/passwd")]
    [InlineData("/etc/passwd")]
    [InlineData("")]
    public async Task ReadNeverLeavesTheRoot(string documentId)
    {
        FileSystemKnowledgeStore store = new(Root);

        Assert.Null(await store.ReadAsync(documentId, Token));
    }

    [Fact]
    public async Task ASearchResultReadsBack()
    {
        // The two built-in tools work as a pair: search names a document, and read opens it.
        FileSystemKnowledgeStore store = new(Root);

        var chunks = await store.SearchAsync("pallet", 5, Token);
        var document = await store.ReadAsync(chunks[0].DocumentId, Token);

        Assert.NotNull(document);
    }
}
