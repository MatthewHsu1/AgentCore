using AgentCore.Application.Embeddings;
using AgentCore.Application.Tests.Embeddings.Fakes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentCore.Application.Tests.Embeddings
{
    /// <summary>
    /// The cache in front of the embedding vendor. A repeat of one text under one model must not
    /// reach the vendor again; a different model must.
    /// </summary>
    public sealed class HybridCachingEmbeddingGeneratorTests
    {
        private static HybridCache NewCache()
        {
            ServiceCollection services = new();
            _ = services.AddHybridCache();
            return services.BuildServiceProvider().GetRequiredService<HybridCache>();
        }

        [Fact]
        public async Task Repeat_of_one_text_skips_the_vendor()
        {
            CountingEmbeddingGenerator vendor = new("model-a");
            using HybridCachingEmbeddingGenerator generator = new(vendor, NewCache());

            GeneratedEmbeddings<Embedding<float>> first = await generator.GenerateAsync(["hello"], cancellationToken: TestContext.Current.CancellationToken);
            GeneratedEmbeddings<Embedding<float>> second = await generator.GenerateAsync(["hello"], cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(1, vendor.TextsEmbedded);
            Assert.Equal(first[0].Vector.ToArray(), second[0].Vector.ToArray());
        }

        [Fact]
        public async Task Only_the_new_text_in_a_batch_reaches_the_vendor()
        {
            CountingEmbeddingGenerator vendor = new("model-a");
            using HybridCachingEmbeddingGenerator generator = new(vendor, NewCache());

            _ = await generator.GenerateAsync(["hello"], cancellationToken: TestContext.Current.CancellationToken);
            GeneratedEmbeddings<Embedding<float>> batch = await generator.GenerateAsync(["hello", "world!"], cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(2, vendor.TextsEmbedded);
            Assert.Equal([5f], batch[0].Vector.ToArray());
            Assert.Equal([6f], batch[1].Vector.ToArray());
        }

        [Fact]
        public async Task A_different_model_gets_its_own_entry()
        {
            CountingEmbeddingGenerator vendor = new("model-a");
            using HybridCachingEmbeddingGenerator generator = new(vendor, NewCache());

            _ = await generator.GenerateAsync(["hello"], cancellationToken: TestContext.Current.CancellationToken);
            _ = await generator.GenerateAsync(
                ["hello"],
                new EmbeddingGenerationOptions { ModelId = "model-b" },
                TestContext.Current.CancellationToken);

            Assert.Equal(2, vendor.TextsEmbedded);
        }

        [Fact]
        public async Task Two_vendors_with_different_default_models_do_not_share_entries()
        {
            HybridCache cache = NewCache();
            CountingEmbeddingGenerator vendorA = new("model-a");
            CountingEmbeddingGenerator vendorB = new("model-b");
            using HybridCachingEmbeddingGenerator generatorA = new(vendorA, cache);
            using HybridCachingEmbeddingGenerator generatorB = new(vendorB, cache);

            _ = await generatorA.GenerateAsync(["hello"], cancellationToken: TestContext.Current.CancellationToken);
            _ = await generatorB.GenerateAsync(["hello"], cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(1, vendorA.TextsEmbedded);
            Assert.Equal(1, vendorB.TextsEmbedded);
        }
    }
}
