using System.ClientModel;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.Application.Secrets;
using Microsoft.Extensions.AI;
using OpenAI;

namespace AgentCore.Infrastructure.Knowledge.VectorData.Zilliz;

/// <summary>
/// The <c>zilliz</c> knowledge vendor: a Zilliz Cloud collection behind the ranking port.
/// </summary>
/// <remarks>
/// <para>
/// This adapter serves <c>providers.knowledge.search</c> and nothing else. D7 keeps the document
/// half in the file store, because path 2 has the vector store return a leaf path that
/// <c>knowledge.read</c> then opens on disk. A document that names <c>zilliz</c> for
/// <c>documents</c> is stopped by <c>CompositeKnowledgeStoreFactory</c> at startup, and
/// <see cref="CreateDocumentsAsync"/> is the second guard behind that one.
/// </para>
/// <para>
/// The adapter owns the vendor only: the cluster URL, the key, the collection, and the embedding
/// model. Section 3.1 fixes the model at <see cref="EmbeddingModel"/> and the width at
/// <see cref="EmbeddingDimensions"/>, so the vectors this host writes and the vectors it searches by
/// are the same shape. Both are constants and neither is a document field.
/// </para>
/// <para>
/// The adapter builds the client, and nothing above it builds one. It asks the pipeline of the host
/// for <see cref="HttpClientName"/>, which is where the connection lifetime and the retry live, and
/// it binds the key into <see cref="ZillizAuthHeaderHandler"/>. <see cref="ZillizCollection"/>
/// therefore holds a client and a collection name, and no credential and no policy of its own.
/// </para>
/// <para>
/// No key appears in this file, and building costs no request. The chain is asked for
/// <see cref="ApiKeySecretName"/> and the <see cref="ApiKeyVariableName"/> variable answers when the
/// chain holds nothing, exactly as <c>OpenAiChatClientAdapter</c> reads its own key. A bad key
/// therefore stops the first search and not the start, and the start reaches no network at all.
/// </para>
/// </remarks>
public sealed class ZillizKnowledgeAdapter : IKnowledgeStoreAdapter
{
    /// <summary>The one <c>kind</c> value this adapter serves.</summary>
    public const string ProviderKind = "zilliz";

    /// <summary>The <c>${secret:name}</c> name the resolver chain is asked for.</summary>
    public const string ApiKeySecretName = "zilliz-api-key";

    /// <summary>The standard Zilliz environment variable, read when the chain holds no name.</summary>
    public const string ApiKeyVariableName = "ZILLIZ_API_KEY";

    /// <summary>The embedding model of section 3.1. It is not a document field.</summary>
    public const string EmbeddingModel = "text-embedding-3-small";

    /// <summary>The embedding width of section 3.1, in dimensions. It is not a document field.</summary>
    public const int EmbeddingDimensions = 1024;

    /// <summary>The JSON Pointer a missing or unreadable cluster URL reports.</summary>
    public const string EndpointPointer = "/providers/knowledge/endpoint";

    /// <summary>The name this adapter opens its client under, on the pipeline of the host.</summary>
    /// <remarks>
    /// The pipeline serves any name and gives each one the same defaults, so this name is chosen
    /// here, beside the vendor it belongs to, and no host registers it in advance.
    /// </remarks>
    public const string HttpClientName = "agentcore.zilliz";

    /// <summary>The deadline of one search, over every attempt the pipeline makes.</summary>
    /// <remarks>
    /// A vector search is a fast operation, and a caller is waiting on the telephone while it runs.
    /// The shipped default of 100 seconds is longer than the call would survive.
    /// </remarks>
    public static readonly TimeSpan SearchDeadline = TimeSpan.FromSeconds(10);

    private readonly IHttpMessageHandlerFactory _handlers;
    
    private readonly IEmbeddingGenerator<string, Embedding<float>>? _embeddings;

    /// <summary>Creates the adapter, over the pipeline the host built.</summary>
    /// <param name="handlers">
    /// The outbound HTTP pipeline. This adapter asks it for <see cref="HttpClientName"/>.
    /// </param>
    /// <param name="embeddings">
    /// The generator to embed a query with, or <see langword="null"/> to build the OpenAI generator
    /// section 3.1 names.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="handlers"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// <para>
    /// A host passes <c>AgentCoreHttpClients</c>, and every request of this vendor then carries the
    /// connection lifetime and the retry that pipeline holds. A test passes a pipeline that answers
    /// offline, so it reaches no network.
    /// </para>
    /// <para>
    /// <b>The pipeline is required rather than optional.</b> A handler built here instead would send
    /// with no retry and no rate limit answer, and nothing would say so. This vendor refuses a search
    /// option it cannot honour for the same reason.
    /// </para>
    /// </remarks>
    public ZillizKnowledgeAdapter(
        IHttpMessageHandlerFactory handlers,
        IEmbeddingGenerator<string, Embedding<float>>? embeddings = null)
    {
        ArgumentNullException.ThrowIfNull(handlers);

        _handlers = handlers;
        _embeddings = embeddings;
    }

    /// <summary>Gets the one <c>kind</c> value this adapter serves.</summary>
    public string Kind => ProviderKind;

    /// <summary>Gets <see langword="true"/>: a vector store is what ranks.</summary>
    public bool CanServeSearch => true;

    /// <summary>Gets <see langword="false"/>: D7 reads a document from the file store.</summary>
    public bool CanServeDocuments => false;

    /// <summary>Opens the collection the document names, and ranks over it.</summary>
    /// <param name="entry">The <c>providers.knowledge</c> block, whose <c>search</c> named this adapter.</param>
    /// <param name="secrets">The chain the two keys resolve through, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the key reads.</param>
    /// <returns>The ranking port.</returns>
    /// <exception cref="ConfigurationLoadException"><c>endpoint</c> is missing, or is not a URL.</exception>
    /// <exception cref="SecretResolutionException">Neither the chain nor the environment holds a key.</exception>
    /// <remarks>
    /// This runs once, while the host starts. It opens no socket: the first search is the first
    /// request, so a host with no route to the cluster still starts.
    /// </remarks>
    public async ValueTask<IKnowledgeRetrievalPort> CreateSearchAsync(
        KnowledgeProviderConfiguration entry,
        ISecretResolverPort? secrets,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // The document is read before any credential, so a document that names no cluster fails on
        // the field it forgot and not on a key it did not need.
        var endpoint = Endpoint(entry);

        var collection = entry.Collection is { Length: > 0 } named
            ? named
            : KnowledgeProviderConfiguration.DefaultCollection;

        var apiKey = await ResolveKeyAsync(secrets, cancellationToken).ConfigureAwait(false);
        var embeddings = _embeddings ?? await OpenAiEmbeddingsAsync(secrets, cancellationToken).ConfigureAwait(false);

        // The key is written onto the request one layer below the connector, so no class that builds
        // a body or reads an answer holds a credential.
        var inner = _handlers.CreateHandler(HttpClientName);

        // The client lives as long as the process, which is what the composite promises every port it
        // builds. The pipeline owns the chain below this handler, and other clients send on the same
        // chain, so this client disposes nothing.
        HttpClient client = new(new ZillizAuthHeaderHandler(apiKey) { InnerHandler = inner }, disposeHandler: false)
        {
            BaseAddress = endpoint,
            Timeout = SearchDeadline,
        };

        return new ZillizRetrievalStore(new ZillizCollection(client, collection), embeddings);
    }

    /// <summary>Refuses the document half.</summary>
    /// <param name="entry">Unused.</param>
    /// <param name="secrets">Unused.</param>
    /// <param name="cancellationToken">Unused.</param>
    /// <returns>Nothing: this member always throws.</returns>
    /// <exception cref="NotSupportedException">Always, because <see cref="CanServeDocuments"/> is false.</exception>
    /// <remarks>
    /// The composite reads <see cref="CanServeDocuments"/> and stops the start before it reaches
    /// here, so this is the guard behind that guard.
    /// </remarks>
    public ValueTask<IDocumentStorePort> CreateDocumentsAsync(
        KnowledgeProviderConfiguration entry,
        ISecretResolverPort? secrets,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException(
            "The zilliz adapter serves providers.knowledge.search only. D7 reads a document from the "
            + "file store, so name kind 'filesystem' for providers.knowledge.documents.");

    /// <summary>Reads the cluster URL out of the document.</summary>
    /// <param name="entry">The <c>providers.knowledge</c> block.</param>
    /// <returns>The URL.</returns>
    /// <exception cref="ConfigurationLoadException">The field is missing, or is not an absolute URL.</exception>
    private static Uri Endpoint(KnowledgeProviderConfiguration entry)
    {
        if (entry.Endpoint is not { Length: > 0 } endpoint || string.IsNullOrWhiteSpace(endpoint))
        {
            throw Fail(
                "providers.knowledge.search is kind: " + ProviderKind + ", and that store needs "
                + "providers.knowledge.endpoint. Write the cluster URL of the Zilliz collection there.");
        }

        return Uri.TryCreate(endpoint, UriKind.Absolute, out var url)
            ? url
            : throw Fail(
                "providers.knowledge.endpoint is '" + endpoint + "', which is not an absolute URL. "
                + "Write the cluster URL of the Zilliz collection, such as https://in03-x.serverless.gcp-us-west1.cloud.zilliz.com.");
    }

    /// <summary>Builds the one exception a bad <c>endpoint</c> uses.</summary>
    /// <param name="message">What is wrong.</param>
    /// <returns>The exception.</returns>
    private static ConfigurationLoadException Fail(string message)
        => new(new ConfigurationError
        {
            Pointer = EndpointPointer,
            Message = message,
            Check = ConfigurationCheck.ReferenceResolution,
        });

    /// <summary>Reads the Zilliz key through the chain and then the environment.</summary>
    /// <param name="secrets">The resolver chain, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The key.</returns>
    /// <exception cref="SecretResolutionException">Neither place holds one.</exception>
    private static async ValueTask<string> ResolveKeyAsync(
        ISecretResolverPort? secrets,
        CancellationToken cancellationToken)
    {
        string? key = null;
        if (secrets is not null)
        {
            key = await secrets.TryResolveAsync(ApiKeySecretName, cancellationToken).ConfigureAwait(false);
        }

        key ??= Environment.GetEnvironmentVariable(ApiKeyVariableName);
        if (key is not { Length: > 0 })
        {
            throw new SecretResolutionException(
                "the Zilliz API key did not resolve. Bind a resolver that holds '" + ApiKeySecretName
                + "', or set the " + ApiKeyVariableName + " variable. This adapter holds no key of its own.");
        }

        return key;
    }

    /// <summary>Builds the OpenAI generator that embeds a query, at the width of section 3.1.</summary>
    /// <param name="secrets">The resolver chain, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels the key read.</param>
    /// <returns>The generator.</returns>
    /// <exception cref="SecretResolutionException">Neither place holds an OpenAI key.</exception>
    /// <remarks>
    /// The OpenAI key is the one <c>OpenAiChatClientAdapter</c> already reads, so a host that talks
    /// to a model holds nothing new to run this store. Building the client opens no socket.
    /// </remarks>
    private static async ValueTask<IEmbeddingGenerator<string, Embedding<float>>> OpenAiEmbeddingsAsync(
        ISecretResolverPort? secrets,
        CancellationToken cancellationToken)
    {
        string? key = null;
        if (secrets is not null)
        {
            key = await secrets
                .TryResolveAsync(Llm.OpenAiChatClientAdapter.ApiKeySecretName, cancellationToken)
                .ConfigureAwait(false);
        }

        key ??= Environment.GetEnvironmentVariable(Llm.OpenAiChatClientAdapter.ApiKeyVariableName);
        if (key is not { Length: > 0 })
        {
            throw new SecretResolutionException(
                "the OpenAI API key did not resolve, and the zilliz store embeds every query with "
                + EmbeddingModel + ". Bind a resolver that holds '"
                + Llm.OpenAiChatClientAdapter.ApiKeySecretName + "', or set the "
                + Llm.OpenAiChatClientAdapter.ApiKeyVariableName + " variable.");
        }

        return new OpenAIClient(new ApiKeyCredential(key))
            .GetEmbeddingClient(EmbeddingModel)
            .AsIEmbeddingGenerator(EmbeddingDimensions);
    }
}
