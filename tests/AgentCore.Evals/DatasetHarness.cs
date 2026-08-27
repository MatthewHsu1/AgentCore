using System.ClientModel;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Evaluation;
using AgentCore.Application.Knowledge;
using AgentCore.Application.Ports;
using AgentCore.Application.Secrets;
using AgentCore.Infrastructure.Knowledge.VectorData.Qdrant;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Reporting;
using Microsoft.Extensions.AI.Evaluation.Reporting.Storage;
using OpenAI;

namespace AgentCore.Evals;

/// <summary>
/// Everything one golden-set retrieval run needs, built once from one configuration document.
/// </summary>
/// <remarks>
/// <para>
/// The harness composes the adapter the shipped host composes, so the score describes the retrieval
/// stack a deployment runs: one <see cref="QdrantKnowledgeAdapter"/>, built from
/// <c>providers.knowledge</c> of the document <c>AGENTCORE_EVAL_CONFIG</c> names.
/// </para>
/// <para>
/// Ruling 14: <see cref="CreateAsync"/> passes <see langword="false"/> for <c>requireScope</c>. A
/// golden set measures recall against the whole corpus a deployment holds, and <see cref="GoldenRow"/>
/// carries no facet a row could be checked against. Forcing every search through one fixed
/// <c>KnowledgeScope</c> would either filter rows about a different facet out of the corpus, or need
/// per-row facet data the row format does not have -- either way the recall number would describe
/// that one scope rather than the corpus the set exists to measure. <c>scopeDeclared</c> stays
/// <see langword="true"/> regardless: it costs nothing here, since <see cref="QdrantKnowledgeAdapter"/>
/// always reports <see cref="IKnowledgeStoreAdapter.CanScope"/>, and it is the safer default if this
/// harness is ever pointed at a second adapter that does not.
/// </para>
/// </remarks>
public sealed class DatasetHarness : IDisposable
{
    /// <summary>The environment variable naming the embedding model this harness queries with.</summary>
    /// <remarks>
    /// <c>providers.knowledge</c> names the store and never the embedder -- the width check in
    /// <see cref="QdrantKnowledgeAdapter.CreateSearchAsync"/> is what proves a deployment's chosen
    /// model matches the collection, not a document field, matching the "AgentCore never creates"
    /// stance <see cref="KnowledgeProviderConfiguration.Collection"/> documents for the store itself.
    /// A deployment's own <c>kb sync</c> run is what actually fixes the model, so this harness has to
    /// be told the same value some other way. It reads it here rather than inventing a schema field
    /// no production host would ever read.
    /// </remarks>
    public const string EmbeddingModelVariable = "AGENTCORE_EVAL_EMBEDDING_MODEL";

    /// <summary>The model used when the environment names none.</summary>
    public const string DefaultEmbeddingModel = "text-embedding-3-small";

    private readonly IKnowledgeRetrievalPort _search;

    private DatasetHarness(
        AgentCoreConfiguration configuration, IKnowledgeRetrievalPort search, ReportingConfiguration reporting)
    {
        Configuration = configuration;
        _search = search;
        Reporting = reporting;
    }

    /// <summary>Gets the document this run reads.</summary>
    public AgentCoreConfiguration Configuration { get; }

    /// <summary>Gets the ranking port <c>providers.knowledge</c> names.</summary>
    public IKnowledgeRetrievalPort Search => _search;

    /// <summary>Gets the reporting configuration the scenario runs write through.</summary>
    public ReportingConfiguration Reporting { get; }

    /// <summary>Builds the harness from the document <c>AGENTCORE_EVAL_CONFIG</c> names.</summary>
    public static async ValueTask<DatasetHarness> CreateAsync(CancellationToken cancellationToken = default)
    {
        var configuration = EvalHarness.LoadConfiguration();
        var embeddings = await BuildEmbeddingsAsync(cancellationToken).ConfigureAwait(false);

        var search = await CompositeKnowledgeStoreFactory
            .CreateAsync(
                configuration,
                EvalHarness.Secrets,
                [new QdrantKnowledgeAdapter(embeddings)],
                embeddings: null,
                scopeDeclared: true,
                requireScope: false,
                cancellationToken)
            .ConfigureAwait(false);

        if (search is null)
        {
            throw new InvalidOperationException(
                "providers.knowledge has to name a store for the golden set to run.");
        }

        return new DatasetHarness(
            configuration,
            search,
            DiskBasedReportingConfiguration.Create(
                storageRootPath: EvalHarness.StorageRoot,
                evaluators: [new DocumentRecallEvaluator()],
                enableResponseCaching: false,
                executionName: EvalHarness.DatasetExecution));
    }

    /// <inheritdoc />
    public void Dispose() => (_search as IDisposable)?.Dispose();

    /// <summary>Builds the embedder this harness queries with, over the OpenAI credential the chat clients share.</summary>
    private static async ValueTask<IEmbeddingGenerator<string, Embedding<float>>> BuildEmbeddingsAsync(
        CancellationToken cancellationToken)
    {
        var apiKey = await EvalHarness.Secrets
            .RequireAsync(
                KnownSecrets.OpenAi,
                because: "The golden set embeds each query before it searches Qdrant.",
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var model = Environment.GetEnvironmentVariable(EmbeddingModelVariable) is { Length: > 0 } named
            ? named
            : DefaultEmbeddingModel;

        return new OpenAIClient(new ApiKeyCredential(apiKey)).GetEmbeddingClient(model).AsIEmbeddingGenerator();
    }
}
