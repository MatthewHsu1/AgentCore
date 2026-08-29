using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Transcript.Memory;
using AgentCore.Application.Transcript;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Transcript;

/// <summary>
/// The sixth vendor seam: <c>providers.transcript</c>, and what a document that names none gets.
/// </summary>
/// <remarks>
/// It reads an absent block the way <c>providers.audit</c> does, and for the same reason: the turn
/// loop writes the words of every call whether or not a document chose where to put them, so the
/// answer is never <see langword="null"/>. These tests pin the parts that are this project's and not
/// a vendor's — that there is always a store, that <c>memory</c> is this library's own name, and that
/// every other kind goes through the shared selector.
/// </remarks>
public sealed class TranscriptStoreFactoryTests
{
    [Fact]
    public async Task OpenAsync_NoTranscriptBlock_GetsTheInProcessStoreAndAsksNoAdapter()
    {
        // Arrange
        var configuration = ConfigurationLoader.LoadYaml(Document());
        FakeCallMessageStoreAdapter adapter = new("postgres");

        // Act
        var store = await TranscriptStoreFactory.OpenAsync(
            configuration, secrets: null, [adapter], TestContext.Current.CancellationToken);

        // Assert
        Assert.IsType<InMemoryTranscriptStore>(store);
        Assert.Equal(0, adapter.Opens);
    }

    [Fact]
    public async Task OpenAsync_TheMemoryKind_GetsTheInProcessStoreWithNoAdapterRegistered()
    {
        // Arrange
        var configuration = ConfigurationLoader.LoadYaml(Document("kind: memory"));

        // Act
        var store = await TranscriptStoreFactory.OpenAsync(
            configuration, secrets: null, [], TestContext.Current.CancellationToken);

        // Assert
        Assert.IsType<InMemoryTranscriptStore>(store);
    }

    [Fact]
    public async Task OpenAsync_AnAdapterClaimingTheMemoryKind_DoesNotTakeOverTheBuiltIn()
    {
        // Arrange
        var configuration = ConfigurationLoader.LoadYaml(Document("kind: memory"));
        FakeCallMessageStoreAdapter impostor = new("memory");

        // Act
        var store = await TranscriptStoreFactory.OpenAsync(
            configuration, secrets: null, [impostor], TestContext.Current.CancellationToken);

        // Assert
        Assert.IsType<InMemoryTranscriptStore>(store);
        Assert.Equal(0, impostor.Opens);
    }

    [Fact]
    public async Task OpenAsync_TheKind_PicksOneAdapterAndLeavesTheOthersAlone()
    {
        // Arrange
        var configuration = ConfigurationLoader.LoadYaml(Document("kind: fake"));
        FakeCallMessageStoreAdapter fake = new("fake");
        FakeCallMessageStoreAdapter other = new("postgres");

        // Act
        var store = await TranscriptStoreFactory.OpenAsync(
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
        FakeCallMessageStoreAdapter adapter = new("fake");

        // Act
        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
            async () => await TranscriptStoreFactory.OpenAsync(
                configuration, secrets: null, [adapter], TestContext.Current.CancellationToken));

        // Assert
        Assert.Contains("postgres", failure.Message, StringComparison.Ordinal);
        Assert.Equal("/providers/transcript/kind", failure.Errors[0].Pointer);
    }

    [Fact]
    public async Task OpenAsync_TwoAdaptersOnOneKind_FailTheStart()
    {
        // Arrange
        var configuration = ConfigurationLoader.LoadYaml(Document("kind: postgres"));
        FakeCallMessageStoreAdapter[] both =
            [new FakeCallMessageStoreAdapter("postgres"), new FakeCallMessageStoreAdapter("postgres")];

        // Act
        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
            async () => await TranscriptStoreFactory.OpenAsync(
                configuration, secrets: null, both, TestContext.Current.CancellationToken));

        // Assert
        Assert.Contains("two adapters", failure.Message, StringComparison.Ordinal);
        Assert.Contains("stores", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>The <c>providers.knowledge</c> line the transcript block is written after.</summary>
    private const string KnowledgeLine = Configuration.ExampleDocument.LastProviderLine;

    /// <summary>Builds the section 8.1 document with one transcript block written into it.</summary>
    /// <param name="entries">
    /// The keys under <c>providers.transcript</c>, one for each line and without indentation. No entry
    /// at all leaves the block out, which is the case that must still produce a store.
    /// </param>
    private static string Document(params string[] entries)
        => entries.Length == 0
            ? Configuration.ExampleDocument.Yaml
            : Configuration.ExampleDocument.Yaml.Replace(
                KnowledgeLine,
                KnowledgeLine + "\n  transcript:\n" + string.Join("\n", entries.Select(entry => "    " + entry)),
                StringComparison.Ordinal);

    /// <summary>An adapter that opens nothing and records that it was asked.</summary>
    private sealed class FakeCallMessageStoreAdapter(string kind) : ITranscriptStoreAdapter
    {
        public string Kind => kind;

        public int Opens { get; private set; }

        /// <summary>The store this adapter hands over. It is a different type from the built-in one.</summary>
        public ITranscriptStore Store { get; } = new FakeCallMessageStore();

        public ValueTask<ITranscriptStore> OpenAsync(
            VendorProviderConfiguration entry,
            ISecretResolverPort? secrets,
            CancellationToken cancellationToken = default)
        {
            Opens++;
            return ValueTask.FromResult(Store);
        }
    }

    /// <summary>A store that keeps nothing, and is a different type from the built-in one.</summary>
    private sealed class FakeCallMessageStore : ITranscriptStore
    {
        public ValueTask AppendAsync(
            IReadOnlyList<CallMessage> messages, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask RewriteAsync(
            string callId, int ordinal, ChatMessage content, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }
}
