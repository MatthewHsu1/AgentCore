using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Evaluation;
using AgentCore.Application.Knowledge;
using AgentCore.Application.Llm;
using AgentCore.Application.Ports;
using AgentCore.AspNetCore.DependencyInjection;
using AgentCore.Infrastructure.Knowledge.FileStore;
using AgentCore.Infrastructure.Knowledge.VectorData.Zilliz;
using AgentCore.Infrastructure.Llm.OpenAI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Quality;
using Microsoft.Extensions.AI.Evaluation.Reporting;
using Microsoft.Extensions.AI.Evaluation.Reporting.Storage;

namespace AgentCore.Evals;

/// <summary>
/// Everything one golden-set run needs, built once from one configuration document.
/// </summary>
/// <remarks>
/// <para>
/// The harness composes the adapters the shipped host composes, so the score describes the stack the
/// deployment runs. It builds the chat clients, the knowledge ports, and the reporting configuration
/// together, because all three read the same document and the chat clients outlive the build.
/// </para>
/// <para>
/// Response caching is on whenever a judge exists. The cache key covers the model and the request, so
/// a rerun that changed neither costs nothing and returns the same score. A changed prompt or a
/// changed model misses the cache and calls the endpoint again, which is correct.
/// </para>
/// </remarks>
public sealed class DatasetHarness : IDisposable
{
    private readonly CompositeChatClientFactory? _clients;
    private readonly AgentCoreHttpClients _httpClients;

    private DatasetHarness(
        AgentCoreConfiguration configuration,
        AgentCoreHttpClients httpClients,
        CompositeChatClientFactory? clients,
        IKnowledgeRetrievalPort search,
        IDocumentStorePort documents,
        ReportingConfiguration reporting)
    {
        Configuration = configuration;
        _httpClients = httpClients;
        _clients = clients;
        Search = search;
        Documents = documents;
        Reporting = reporting;
    }

    /// <summary>Gets the document this run reads.</summary>
    public AgentCoreConfiguration Configuration { get; }

    /// <summary>Gets the ranking port <c>providers.knowledge.search</c> names.</summary>
    public IKnowledgeRetrievalPort Search { get; }

    /// <summary>Gets the document port <c>providers.knowledge.documents</c> names.</summary>
    public IDocumentStorePort Documents { get; }

    /// <summary>Gets the reporting configuration the scenario runs write through.</summary>
    public ReportingConfiguration Reporting { get; }

    /// <summary>Gets whether the document names a judge, so a judged metric can run.</summary>
    public bool HasJudge => Configuration.Evaluation?.Judge is not null;

    /// <summary>Gets the client that writes a reply, or <see langword="null"/> when no judge exists.</summary>
    /// <remarks>
    /// The reply model is the one the agents already read. The generation metric measures the reply
    /// this model writes over the passages the search returned, which is the generation half of the
    /// same pipeline the deployment runs.
    /// </remarks>
    public IChatClient? Reply
        => _clients?.GetChatClient(Configuration.Agents?.Defaults?.Model);

    /// <summary>Builds the harness from the document <c>AGENTCORE_EVAL_CONFIG</c> names.</summary>
    public static async ValueTask<DatasetHarness> CreateAsync(CancellationToken cancellationToken = default)
    {
        var configuration = EvalHarness.LoadConfiguration();

        // The one outbound pipeline the host builds. A vendor knowledge adapter needs it, and an
        // adapter no field names is never asked to build anything, so a document that reads a
        // directory still opens no socket.
        AgentCoreHttpClients httpClients = new();

        try
        {
            var (search, documents) = await CompositeKnowledgeStoreFactory
                .CreateAsync(
                    configuration,
                    EvalHarness.Secrets,
                    [new FileSystemKnowledgeAdapter(), new ZillizKnowledgeAdapter(httpClients)],
                    includeSearch: true,
                    includeDocuments: true,
                    cancellationToken)
                .ConfigureAwait(false);

            if (search is null || documents is null)
            {
                throw new InvalidOperationException(
                    "providers.knowledge has to name both search and documents for the offline set to run.");
            }

            List<IEvaluator> evaluators = [new DocumentRecallEvaluator(), new FaultCodeEvaluator()];

            if (configuration.Evaluation?.Judge is not { } judge)
            {
                // A document that names no judge still measures retrieval and fault codes. It reads
                // no key for evaluation, and it calls nothing.
                return new DatasetHarness(
                    configuration,
                    httpClients,
                    clients: null,
                    search,
                    documents,
                    DiskBasedReportingConfiguration.Create(
                        storageRootPath: EvalHarness.StorageRoot,
                        evaluators: evaluators,
                        enableResponseCaching: false,
                        executionName: EvalHarness.DatasetExecution));
            }

            var clients = await CompositeChatClientFactory
                .CreateAsync(configuration, EvalHarness.Secrets, [new OpenAiChatClientAdapter()], cancellationToken)
                .ConfigureAwait(false);

            evaluators.Add(new GroundednessEvaluator());

            return new DatasetHarness(
                configuration,
                httpClients,
                clients,
                search,
                documents,
                DiskBasedReportingConfiguration.Create(
                    storageRootPath: EvalHarness.StorageRoot,
                    evaluators: evaluators,
                    chatConfiguration: new ChatConfiguration(clients.GetChatClient(judge)),
                    enableResponseCaching: true,
                    executionName: EvalHarness.DatasetExecution));
        }
        catch
        {
            httpClients.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _clients?.Dispose();
        _httpClients.Dispose();
    }
}
