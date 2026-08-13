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
        FileSystemKnowledgeStore store = new(new KnowledgeProviderConfiguration { Documents = "filesystem", Root = "./kb" });

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

    [Fact]
    public async Task ListWithNoPattern_NamesEveryDocumentInOrdinalOrder()
    {
        FileSystemKnowledgeStore store = new(Root);

        var listing = await store.ListAsync(cancellationToken: Token);

        Assert.Equal(["policies/shipping.md", "returns.md"], listing.DocumentIds);
        Assert.False(listing.Truncated);
    }

    [Fact]
    public async Task ListWithAPattern_NamesOnlyWhatThePatternKeeps()
    {
        FileSystemKnowledgeStore store = new(Root);

        var listing = await store.ListAsync("policies/**/*.md", Token);

        Assert.Equal("policies/shipping.md", Assert.Single(listing.DocumentIds));
        Assert.False(listing.Truncated);
    }

    [Fact]
    public async Task ListWithAPatternThatMatchesNothing_NamesNothing()
    {
        FileSystemKnowledgeStore store = new(Root);

        var listing = await store.ListAsync("manuals/**/*.md", Token);

        Assert.Empty(listing.DocumentIds);
        Assert.False(listing.Truncated);
    }

    [Fact]
    public async Task ListOverARootThatIsNotThere_NamesNothing()
    {
        FileSystemKnowledgeStore store = new(Path.Combine(AppContext.BaseDirectory, "kb-missing"));

        var listing = await store.ListAsync(cancellationToken: Token);

        Assert.Empty(listing.DocumentIds);
        Assert.False(listing.Truncated);
    }

    [Theory]
    [InlineData("../*.md")]
    [InlineData("../**/*.md")]
    [InlineData("/etc/*")]
    public async Task ListNeverLeavesTheRoot(string pattern)
    {
        FileSystemKnowledgeStore store = new(Root);

        var listing = await store.ListAsync(pattern, Token);

        Assert.Empty(listing.DocumentIds);
    }

    [Fact]
    public async Task ListCapsTheNumberOfIdsItNames()
    {
        var root = CreateTree(FileSystemKnowledgeStore.MaxListResults + 1, "A treadmill.");
        try
        {
            FileSystemKnowledgeStore store = new(root);

            var listing = await store.ListAsync(cancellationToken: Token);

            Assert.Equal(FileSystemKnowledgeStore.MaxListResults, listing.DocumentIds.Count);
            Assert.True(listing.Truncated);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task GrepNamesTheDocumentTheLineAndTheLineNumber()
    {
        FileSystemKnowledgeStore store = new(Root);

        var result = await store.GrepAsync("pallet", cancellationToken: Token);

        var match = Assert.Single(result.Matches);
        Assert.Equal("policies/shipping.md", match.DocumentId);
        Assert.Equal(3, match.LineNumber);
        Assert.StartsWith("A treadmill ships on a pallet", match.Line, StringComparison.Ordinal);
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task GrepReadsEveryDocumentThePatternIsNotHeldFrom()
    {
        FileSystemKnowledgeStore store = new(Root);

        var result = await store.GrepAsync("treadmill", cancellationToken: Token);

        Assert.Equal(["policies/shipping.md", "returns.md"], result.Matches.Select(match => match.DocumentId));
    }

    [Fact]
    public async Task GrepWithAGlob_ReadsOnlyWhatTheGlobKeeps()
    {
        FileSystemKnowledgeStore store = new(Root);

        var result = await store.GrepAsync("treadmill", "policies/**", Token);

        Assert.Equal("policies/shipping.md", Assert.Single(result.Matches).DocumentId);
    }

    [Fact]
    public async Task GrepThatMatchesNothing_ReturnsNothing()
    {
        FileSystemKnowledgeStore store = new(Root);

        var result = await store.GrepAsync("aardvark", cancellationToken: Token);

        Assert.Empty(result.Matches);
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task GrepWithAGlobThatLeavesTheRoot_ReturnsNothing()
    {
        FileSystemKnowledgeStore store = new(Root);

        var result = await store.GrepAsync("treadmill", "../**/*.md", Token);

        Assert.Empty(result.Matches);
    }

    [Fact]
    public async Task GrepWithAPatternThatIsNotARegularExpression_Throws()
    {
        // Section 8.7: the built-in tool turns the failure into an error result, so the store throws.
        FileSystemKnowledgeStore store = new(Root);

        await Assert.ThrowsAnyAsync<ArgumentException>(
            async () => await store.GrepAsync("[unclosed", cancellationToken: Token));
    }

    [Fact]
    public async Task GrepCapsTheNumberOfMatchesItReturns()
    {
        var root = CreateTree(FileSystemKnowledgeStore.MaxGrepMatches + 1, "A treadmill.");
        try
        {
            FileSystemKnowledgeStore store = new(root);

            var result = await store.GrepAsync("treadmill", cancellationToken: Token);

            Assert.Equal(FileSystemKnowledgeStore.MaxGrepMatches, result.Matches.Count);
            Assert.True(result.Truncated);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Writes a tree of one-line documents, and answers where it wrote them.</summary>
    private static string CreateTree(int documents, string line)
    {
        var root = Path.Combine(Path.GetTempPath(), "agentcore-kb-" + Path.GetRandomFileName());
        Directory.CreateDirectory(root);

        for (var document = 0; document < documents; document++)
        {
            File.WriteAllText(Path.Combine(root, $"doc-{document:D4}.md"), line);
        }

        return root;
    }
}
