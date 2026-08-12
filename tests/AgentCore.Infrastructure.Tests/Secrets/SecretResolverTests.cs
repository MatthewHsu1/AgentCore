using AgentCore.Application.Ports;
using AgentCore.Application.Secrets;
using AgentCore.Infrastructure.Secrets;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Secrets;

/// <summary>
/// The adapters behind <see cref="ISecretResolverPort"/>, and the chain that orders them.
/// </summary>
/// <remarks>
/// The chain is ordered and open: the host lists the links it wants, the first hit wins, and a new
/// store joins by adding a link. Nothing here claims a fixed number of steps.
/// </remarks>
public sealed class SecretResolverTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---------------------------------------------------------------------------------------------
    // The environment.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task TheEnvironmentResolver_ReadsTheNameTheDocumentWrote()
    {
        EnvironmentSecretResolver resolver = new(name => name == "orders-api-key" ? "sk-1" : null);

        Assert.Equal("sk-1", await resolver.TryResolveAsync("orders-api-key", Token));
    }

    [Fact]
    public async Task TheEnvironmentResolver_AlsoReadsTheShoutingForm()
    {
        // A shell variable is rarely called orders-api-key. The resolver tries the name first, then
        // the ORDERS_API_KEY form every deployment already writes.
        EnvironmentSecretResolver resolver = new(name => name == "ORDERS_API_KEY" ? "sk-2" : null);

        Assert.Equal("sk-2", await resolver.TryResolveAsync("orders-api-key", Token));
    }

    [Fact]
    public async Task TheEnvironmentResolver_AnswersNullForAnUnknownName()
    {
        EnvironmentSecretResolver resolver = new(_ => null);

        Assert.Null(await resolver.TryResolveAsync("orders-api-key", Token));
    }

    [Fact]
    public void TheEnvironmentVariableName_ShoutsAndReplacesEverySeparator()
    {
        Assert.Equal("ORDERS_API_KEY", EnvironmentSecretResolver.ToVariableName("orders-api-key"));
        Assert.Equal("ORDERS_API_KEY", EnvironmentSecretResolver.ToVariableName("orders.api.key"));
        Assert.Equal("ORDERS_API_KEY", EnvironmentSecretResolver.ToVariableName("orders/api/key"));
    }

    // ---------------------------------------------------------------------------------------------
    // A directory of files, the /run/secrets convention.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task TheFileResolver_ReadsOneFilePerName()
    {
        using TemporaryDirectory directory = new();
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "orders-api-key"), "sk-3\n", Token);

        FileSecretResolver resolver = new(directory.Path);

        // The trailing newline an editor writes is not part of the credential.
        Assert.Equal("sk-3", await resolver.TryResolveAsync("orders-api-key", Token));
    }

    [Fact]
    public async Task TheFileResolver_AnswersNullWhenTheFileIsNotThere()
    {
        using TemporaryDirectory directory = new();
        FileSecretResolver resolver = new(directory.Path);

        Assert.Null(await resolver.TryResolveAsync("orders-api-key", Token));
    }

    [Fact]
    public async Task TheFileResolver_AnswersNullWhenTheDirectoryIsNotThere()
    {
        FileSecretResolver resolver = new(Path.Combine(Path.GetTempPath(), "agentcore-no-such-directory"));

        Assert.Null(await resolver.TryResolveAsync("orders-api-key", Token));
    }

    [Theory]
    [InlineData("../../../etc/passwd")]
    [InlineData("nested/name")]
    [InlineData("..")]
    [InlineData("")]
    public async Task TheFileResolver_ReadsNothingOutsideItsDirectory(string name)
    {
        using TemporaryDirectory directory = new();
        FileSecretResolver resolver = new(directory.Path);

        Assert.Null(await resolver.TryResolveAsync(name, Token));
    }

    // ---------------------------------------------------------------------------------------------
    // The chain.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task TheChain_TakesTheFirstHit()
    {
        ChainedSecretResolver chain = new(
        [
            new EnvironmentSecretResolver(_ => null),
            new EnvironmentSecretResolver(_ => "second"),
            new EnvironmentSecretResolver(_ => "third"),
        ]);

        Assert.Equal("second", await chain.TryResolveAsync("orders-api-key", Token));
        Assert.Equal(3, chain.Count);
    }

    [Fact]
    public async Task TheChain_AnswersNullWhenNoLinkHoldsTheName()
    {
        ChainedSecretResolver chain = new([new EnvironmentSecretResolver(_ => null)]);

        Assert.Null(await chain.TryResolveAsync("orders-api-key", Token));
    }

    [Fact]
    public async Task TheChain_TakesMoreLinksWithoutChanging()
    {
        // "Open to more links" is the whole design. A host that adds a vault adds a link.
        using TemporaryDirectory directory = new();
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "orders-api-key"), "from-file", Token);

        ChainedSecretResolver chain = new(
        [
            new EnvironmentSecretResolver(_ => null),
            new FileSecretResolver(directory.Path),
        ]);

        Assert.Equal("from-file", await chain.TryResolveAsync("orders-api-key", Token));
    }

    [Fact]
    public async Task TheChain_WithNoLink_AnswersNull()
    {
        ChainedSecretResolver chain = new([]);

        Assert.Null(await chain.TryResolveAsync("orders-api-key", Token));
    }

    // ---------------------------------------------------------------------------------------------
    // The whole path, from the document to the header value.
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task AValueNeverReachesTheFailureMessage()
    {
        using TemporaryDirectory directory = new();
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "known"), "sk-live-secret", Token);

        ChainedSecretResolver chain = new([new FileSecretResolver(directory.Path)]);
        var known = await chain.TryResolveAsync("known", Token);

        var failure = Assert.Throws<SecretResolutionException>(
            () => ResolvedSecrets.Create([new KeyValuePair<string, string>("known", known!)])
                .Format(Application.Configuration.Parsing.SecretTemplate.Parse("${secret:unknown}")));

        Assert.DoesNotContain("sk-live-secret", failure.ToString(), StringComparison.Ordinal);
    }

    /// <summary>A directory that deletes itself when the test ends.</summary>
    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "agentcore-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
