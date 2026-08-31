using AgentCore.Application.Calls;
using AgentCore.Application.Calls.Memory;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.TestSupport;
using Xunit;

namespace AgentCore.Application.Tests.Calls;

/// <summary>
/// The store vendor seam: <c>providers.calls</c>, and what a document that names none gets.
/// </summary>
/// <remarks>
/// The answer is never <see langword="null"/>: a call's row and its words must have somewhere to go
/// whether or not a document chose where. These tests pin the parts that are this project's and not a vendor's --
/// that there is always a store, that <c>memory</c> is this library's own name, and that every other
/// kind goes through the shared selector.
/// </remarks>
public sealed class CallStoreFactoryTests
{
    [Fact]
    public async Task OpenAsync_NoCallsBlock_GetsTheInProcessStoreAndAsksNoAdapter()
    {
        // Arrange
        var configuration = ConfigurationLoader.LoadYaml(Document());
        FakeCallStoreAdapter adapter = new("postgres");

        // Act
        var store = await CallStoreFactory.OpenAsync(
            configuration, secrets: null, [adapter], TestContext.Current.CancellationToken);

        // Assert
        Assert.IsType<InMemoryCallStore>(store);
        Assert.Equal(0, adapter.Opens);
    }

    [Fact]
    public async Task OpenAsync_TheMemoryKind_GetsTheInProcessStoreWithNoAdapterRegistered()
    {
        // Arrange
        var configuration = ConfigurationLoader.LoadYaml(Document("kind: memory"));

        // Act
        var store = await CallStoreFactory.OpenAsync(
            configuration, secrets: null, [], TestContext.Current.CancellationToken);

        // Assert
        Assert.IsType<InMemoryCallStore>(store);
    }

    [Fact]
    public async Task OpenAsync_AnAdapterClaimingTheMemoryKind_DoesNotTakeOverTheBuiltIn()
    {
        // Arrange
        var configuration = ConfigurationLoader.LoadYaml(Document("kind: memory"));
        FakeCallStoreAdapter impostor = new("memory");

        // Act
        var store = await CallStoreFactory.OpenAsync(
            configuration, secrets: null, [impostor], TestContext.Current.CancellationToken);

        // Assert
        Assert.IsType<InMemoryCallStore>(store);
        Assert.Equal(0, impostor.Opens);
    }

    [Fact]
    public async Task OpenAsync_TheKind_PicksOneAdapterAndLeavesTheOthersAlone()
    {
        // Arrange
        var configuration = ConfigurationLoader.LoadYaml(Document("kind: fake"));
        FakeCallStoreAdapter fake = new("fake");
        FakeCallStoreAdapter other = new("postgres");

        // Act
        var store = await CallStoreFactory.OpenAsync(
            configuration, secrets: null, [other, fake], TestContext.Current.CancellationToken);

        // Assert
        Assert.Same(fake.Store, store);
        Assert.Equal(0, other.Opens);
    }

    [Fact]
    public async Task OpenAsync_AKindNoAdapterServes_FailsTheStartAndNamesWhatIsRegistered()
    {
        // Arrange
        var configuration = ConfigurationLoader.LoadYaml(Document("kind: postgres"));
        FakeCallStoreAdapter adapter = new("fake");

        // Act
        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
            async () => await CallStoreFactory.OpenAsync(
                configuration, secrets: null, [adapter], TestContext.Current.CancellationToken));

        // Assert
        Assert.Contains("postgres", failure.Message, StringComparison.Ordinal);
        Assert.Equal("/providers/calls/kind", failure.Errors[0].Pointer);
    }

    [Fact]
    public async Task OpenAsync_TwoAdaptersOnOneKind_FailTheStart()
    {
        // Arrange
        var configuration = ConfigurationLoader.LoadYaml(Document("kind: postgres"));
        FakeCallStoreAdapter[] both =
            [new FakeCallStoreAdapter("postgres"), new FakeCallStoreAdapter("postgres")];

        // Act
        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
            async () => await CallStoreFactory.OpenAsync(
                configuration, secrets: null, both, TestContext.Current.CancellationToken));

        // Assert
        Assert.Contains("two adapters", failure.Message, StringComparison.Ordinal);
        Assert.Contains("stores", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>The <c>providers.knowledge</c> line the calls block is written after.</summary>
    private const string KnowledgeLine = Configuration.ExampleDocument.LastProviderLine;

    /// <summary>Builds the section 8.1 document with one calls block written into it.</summary>
    /// <param name="entries">
    /// The keys under <c>providers.calls</c>, one for each line and without indentation. No entry
    /// at all leaves the block out, which is the case that must still produce a store.
    /// </param>
    private static string Document(params string[] entries)
        => entries.Length == 0
            ? Configuration.ExampleDocument.Yaml
            : Configuration.ExampleDocument.Yaml.Replace(
                KnowledgeLine,
                KnowledgeLine + "\n  calls:\n" + string.Join("\n", entries.Select(entry => "    " + entry)),
                StringComparison.Ordinal);

    /// <summary>An adapter that opens nothing and records that it was asked.</summary>
    private sealed class FakeCallStoreAdapter(string kind) : ICallStoreAdapter
    {
        public string Kind => kind;

        public int Opens { get; private set; }

        /// <summary>The store this adapter hands over. It is a different type from the built-in one.</summary>
        public ICallStore Store { get; } = new FakeCallStore();

        public ValueTask<ICallStore> OpenAsync(
            VendorProviderConfiguration entry,
            ISecretResolverPort? secrets,
            CancellationToken cancellationToken = default)
        {
            Opens++;
            return ValueTask.FromResult(Store);
        }
    }

    /// <summary>A store that is a different type from the built-in one, and does nothing else.</summary>
    private sealed class FakeCallStore() : DelegatingCallStore(new InMemoryCallStore());
}
