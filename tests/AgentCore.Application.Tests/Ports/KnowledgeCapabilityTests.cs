using AgentCore.Application.Ports;
using AgentCore.Domain.Knowledge;

using Xunit;

namespace AgentCore.Application.Tests.Ports;

/// <summary>
/// Asking a knowledge store what else it can do.
/// </summary>
/// <remarks>
/// Search is the one thing every store owes. Reading a facet vocabulary, or whole cards by an
/// exact facet value, are capabilities a store may or may not serve, and a caller has to be able
/// to ask. The question goes to the store itself, so one object is the only source of truth and a
/// store cannot claim a capability it does not have.
/// </remarks>
public sealed class KnowledgeCapabilityTests
{
    [Fact]
    public void AStoreServingAnExtension_HandsItBack()
    {
        IKnowledgeRetrievalPort store = new VocabularyStore();

        Assert.Same(store, store.GetService(typeof(IFacetVocabularyPort)));
    }

    [Fact]
    public void AStoreServingNoExtension_AnswersNull()
    {
        IKnowledgeRetrievalPort store = new SearchOnlyStore();

        Assert.Null(store.GetService(typeof(IFacetVocabularyPort)));
    }

    [Fact]
    public void AStoreAskedForItself_HandsItselfBack()
    {
        IKnowledgeRetrievalPort store = new SearchOnlyStore();

        Assert.Same(store, store.GetService(typeof(IKnowledgeRetrievalPort)));
    }

    [Fact]
    public void AKeyedRequest_AnswersNull()
    {
        // A store that serves no keyed capability answers nothing rather than ignoring the key and
        // handing back the unkeyed one, which would be a different object than the caller asked for.
        IKnowledgeRetrievalPort store = new VocabularyStore();

        Assert.Null(store.GetService(typeof(IFacetVocabularyPort), "replica"));
    }

    [Fact]
    public void ANullType_Throws()
    {
        IKnowledgeRetrievalPort store = new SearchOnlyStore();

        Assert.Throws<ArgumentNullException>(() => store.GetService(null!));
    }

    [Fact]
    public void TheGenericHelper_HandsBackTheTypedExtension()
    {
        IKnowledgeRetrievalPort store = new VocabularyStore();

        Assert.Same(store, store.GetService<IFacetVocabularyPort>());
    }

    [Fact]
    public void TheGenericHelper_AnswersNullWhenTheStoreServesNothing()
    {
        IKnowledgeRetrievalPort store = new SearchOnlyStore();

        Assert.Null(store.GetService<IFacetVocabularyPort>());
    }

    [Fact]
    public void AWrapperThatForwards_FindsTheCapabilityInside()
    {
        // The whole reason the question goes through a method and not a cast. A wrapper does not
        // implement what it wraps, so 'is IFacetVocabularyPort' answers false on one and the
        // capability disappears. Forwarding the question finds it.
        var inner = new VocabularyStore();
        IKnowledgeRetrievalPort wrapper = new ForwardingStore(inner);

        Assert.False(wrapper is IFacetVocabularyPort);
        Assert.Same(inner, wrapper.GetService<IFacetVocabularyPort>());
    }

    /// <summary>A store that ranks and nothing else.</summary>
    private sealed class SearchOnlyStore : IKnowledgeRetrievalPort
    {
        public ValueTask<IReadOnlyList<KnowledgeCard>> SearchAsync(
            string query, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<KnowledgeCard>>([]);
    }

    /// <summary>A store that also reads a facet vocabulary.</summary>
    private sealed class VocabularyStore : IKnowledgeRetrievalPort, IFacetVocabularyPort
    {
        public ValueTask<IReadOnlyList<KnowledgeCard>> SearchAsync(
            string query, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<KnowledgeCard>>([]);

        public ValueTask<IReadOnlyList<string>> ReadAsync(
            string path, int limit, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<string>>([]);
    }

    /// <summary>A store wrapped in another, as a cache or a log would wrap one.</summary>
    private sealed class ForwardingStore(IKnowledgeRetrievalPort inner) : IKnowledgeRetrievalPort
    {
        public ValueTask<IReadOnlyList<KnowledgeCard>> SearchAsync(
            string query, CancellationToken cancellationToken = default)
            => inner.SearchAsync(query, cancellationToken);

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            ArgumentNullException.ThrowIfNull(serviceType);

            return serviceKey is null && serviceType.IsInstanceOfType(this)
                ? this
                : inner.GetService(serviceType, serviceKey);
        }
    }
}
