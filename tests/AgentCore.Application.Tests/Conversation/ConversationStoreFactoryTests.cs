using AgentCore.Application.Conversation;
using AgentCore.Application.Conversation.Memory;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.TestSupport;
using Xunit;

namespace AgentCore.Application.Tests.Conversation
{
    /// <summary>
    /// The store vendor seam: <c>providers.conversations</c>, and what a document that names none gets.
    /// </summary>
    /// <remarks>
    /// The answer is never <see langword="null"/>: a conversation's row and its words must have somewhere to go
    /// whether or not a document chose where. These tests pin the parts that are this project's and not a vendor's --
    /// that there is always a store, that <c>memory</c> is this library's own name, and that every other
    /// kind goes through the shared selector.
    /// </remarks>
    public sealed class ConversationStoreFactoryTests
    {
        [Fact]
        public async Task OpenAsync_NoConversationsBlock_GetsTheInProcessStoreAndAsksNoAdapter()
        {
            // Arrange
            AgentCoreConfiguration configuration = ConfigurationLoader.LoadYaml(Document());
            FakeConversationStoreAdapter adapter = new("postgres");

            // Act
            IConversationStore store = await ConversationStoreFactory.OpenAsync(
                configuration, secrets: null, [adapter], TestContext.Current.CancellationToken);

            // Assert
            _ = Assert.IsType<InMemoryConversationStore>(store);
            Assert.Equal(0, adapter.Opens);
        }

        [Fact]
        public async Task OpenAsync_TheMemoryKind_GetsTheInProcessStoreWithNoAdapterRegistered()
        {
            // Arrange
            AgentCoreConfiguration configuration = ConfigurationLoader.LoadYaml(Document("kind: memory"));

            // Act
            IConversationStore store = await ConversationStoreFactory.OpenAsync(
                configuration, secrets: null, [], TestContext.Current.CancellationToken);

            // Assert
            _ = Assert.IsType<InMemoryConversationStore>(store);
        }

        [Fact]
        public async Task OpenAsync_AnAdapterClaimingTheMemoryKind_DoesNotTakeOverTheBuiltIn()
        {
            // Arrange
            AgentCoreConfiguration configuration = ConfigurationLoader.LoadYaml(Document("kind: memory"));
            FakeConversationStoreAdapter impostor = new("memory");

            // Act
            IConversationStore store = await ConversationStoreFactory.OpenAsync(
                configuration, secrets: null, [impostor], TestContext.Current.CancellationToken);

            // Assert
            _ = Assert.IsType<InMemoryConversationStore>(store);
            Assert.Equal(0, impostor.Opens);
        }

        [Fact]
        public async Task OpenAsync_TheKind_PicksOneAdapterAndLeavesTheOthersAlone()
        {
            // Arrange
            AgentCoreConfiguration configuration = ConfigurationLoader.LoadYaml(Document("kind: fake"));
            FakeConversationStoreAdapter fake = new("fake");
            FakeConversationStoreAdapter other = new("postgres");

            // Act
            IConversationStore store = await ConversationStoreFactory.OpenAsync(
                configuration, secrets: null, [other, fake], TestContext.Current.CancellationToken);

            // Assert
            Assert.Same(fake.Store, store);
            Assert.Equal(0, other.Opens);
        }

        [Fact]
        public async Task OpenAsync_AKindNoAdapterServes_FailsTheStartAndNamesWhatIsRegistered()
        {
            // Arrange
            AgentCoreConfiguration configuration = ConfigurationLoader.LoadYaml(Document("kind: postgres"));
            FakeConversationStoreAdapter adapter = new("fake");

            // Act
            ConfigurationLoadException failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
                async () => await ConversationStoreFactory.OpenAsync(
                    configuration, secrets: null, [adapter], TestContext.Current.CancellationToken));

            // Assert
            Assert.Contains("postgres", failure.Message, StringComparison.Ordinal);
            Assert.Equal("/providers/conversations/kind", failure.Errors[0].Pointer);
        }

        [Fact]
        public async Task OpenAsync_TwoAdaptersOnOneKind_FailTheStart()
        {
            // Arrange
            AgentCoreConfiguration configuration = ConfigurationLoader.LoadYaml(Document("kind: postgres"));
            FakeConversationStoreAdapter[] both =
                [new FakeConversationStoreAdapter("postgres"), new FakeConversationStoreAdapter("postgres")];

            // Act
            ConfigurationLoadException failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
                async () => await ConversationStoreFactory.OpenAsync(
                    configuration, secrets: null, both, TestContext.Current.CancellationToken));

            // Assert
            Assert.Contains("two adapters", failure.Message, StringComparison.Ordinal);
            Assert.Contains("stores", failure.Message, StringComparison.Ordinal);
        }

        /// <summary>The <c>providers.knowledge</c> line the conversations block is written after.</summary>
        private const string KnowledgeLine = Configuration.ExampleDocument.LastProviderLine;

        /// <summary>Builds the section 8.1 document with one conversations block written into it.</summary>
        /// <param name="entries">
        /// The keys under <c>providers.conversations</c>, one for each line and without indentation. No entry
        /// at all leaves the block out, which is the case that must still produce a store.
        /// </param>
        private static string Document(params string[] entries)
        {
            return entries.Length == 0
                        ? Configuration.ExampleDocument.Yaml
                        : Configuration.ExampleDocument.Yaml.Replace(
                            KnowledgeLine,
                            KnowledgeLine + "\n  conversations:\n" + string.Join("\n", entries.Select(entry => "    " + entry)),
                            StringComparison.Ordinal);
        }

        /// <summary>An adapter that opens nothing and records that it was asked.</summary>
        private sealed class FakeConversationStoreAdapter(string kind) : IConversationStoreAdapter
        {
            public string Kind => kind;

            public int Opens { get; private set; }

            /// <summary>The store this adapter hands over. It is a different type from the built-in one.</summary>
            public IConversationStore Store { get; } = new FakeConversationStore();

            public ValueTask<IConversationStore> OpenAsync(
                VendorProviderConfiguration entry,
                ISecretResolverPort? secrets,
                CancellationToken cancellationToken = default)
            {
                Opens++;
                return ValueTask.FromResult(Store);
            }
        }

        /// <summary>A store that is a different type from the built-in one, and does nothing else.</summary>
        private sealed class FakeConversationStore() : DelegatingConversationStore(new InMemoryConversationStore());
    }
}
