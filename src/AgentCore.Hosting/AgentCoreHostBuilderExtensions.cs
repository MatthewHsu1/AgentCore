using System.Text.Json.Nodes;
using AgentCore.AspNetCore.DependencyInjection;
using AgentCore.AspNetCore.Vendors.TelnyxRelay;
using AgentCore.Hosting.Secrets;
using AgentCore.Infrastructure.Audit.Postgres;
using AgentCore.Infrastructure.Embeddings.OpenAI;
using AgentCore.Infrastructure.Evaluation.OpenAiModeration;
using AgentCore.Infrastructure.Knowledge.VectorData.Qdrant;
using AgentCore.Infrastructure.Llm.OpenAI;
using AgentCore.Infrastructure.Secrets;
using AgentCore.Infrastructure.Telemetry.Grafana;
using AgentCore.Infrastructure.Tools;
using AgentCore.Infrastructure.Transcript.Postgres;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentCore.Hosting;

/// <summary>
/// Registers everything AgentCore needs to run, in one call.
/// </summary>
public static class AgentCoreHostBuilderExtensions
{
    /// <summary>The configuration key that names the document to load.</summary>
    public const string ConfigurationPathKey = "AgentCore:ConfigurationPath";

    /// <summary>The document loaded when <see cref="ConfigurationPathKey"/> names none.</summary>
    public const string DefaultConfigurationPath = "config/example.yaml";

    /// <summary>The <c>binds:</c> name the shipped example document declares.</summary>
    public const string CreateCaseBinding = "CreateCase";

    /// <summary>The name a <c>transport: http</c> MCP server's handler chain is opened under.</summary>
    public const string McpHttpClientName = "agentcore.mcp";

    /// <summary>Registers every vendor seam, and loads the document when the host starts.</summary>
    /// <param name="builder">The host being built.</param>
    /// <param name="configure">
    /// The host's own word on the options, run after the defaults below and therefore winning over
    /// every one of them. A host uses it to bind a <c>kind: binding</c> delegate, to add a vendor
    /// this library does not name, or to replace the document, the secret resolver, the logger
    /// factory, or any vendor seam.
    /// </param>
    /// <returns>The same builder, so a host chains its calls.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    public static WebApplicationBuilder AddAgentCoreHost(
        this WebApplicationBuilder builder,
        Action<AgentCoreOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Logging.AddConsole();

        builder.Services.AddSingleton(provider => new AgentCoreHttpClients(
            loggers: provider.GetRequiredService<ILoggerFactory>()));

        builder.Services
            .AddOptions<AgentCoreOptions>()
            .Configure<AgentCoreHttpClients, IConfiguration, ILoggerFactory>(
                (options, httpClients, hostConfiguration, loggers) =>
                    Configure(hostConfiguration, options, httpClients, loggers));

        builder.Services.AddAgentCore(options => configure?.Invoke(options));

        builder.Services.PostConfigure<AgentCoreOptions>(FinishConfiguring);

        builder.Services.AddAgentCoreWebSockets();

        return builder;
    }

    /// <summary>Fills the options with this library's defaults.</summary>
    /// <param name="hostConfiguration">The host's own configuration, read for the document path and for secrets.</param>
    /// <param name="options">The options <see cref="AgentCoreServiceCollectionExtensions.AddAgentCore"/> registered.</param>
    /// <param name="httpClients">The one outbound pipeline every adapter shares.</param>
    /// <param name="loggers">The host's own logging, for the adapters that report what they are doing.</param>
    /// <remarks>
    /// The host's own <c>configure</c> callback is registered after this one, and configure
    /// callbacks run in registration order — so the host still has the last word, which it has to
    /// have: every <c>Use*</c> seam is a setter rather than a list, so whoever writes last wins.
    /// </remarks>
    private static void Configure(
        IConfiguration hostConfiguration,
        AgentCoreOptions options,
        AgentCoreHttpClients httpClients,
        ILoggerFactory loggers)
    {
        options.ConfigurationPath =
            hostConfiguration[ConfigurationPathKey] ?? DefaultConfigurationPath;

        options.SecretResolver = new ChainedSecretResolver(
        [
            new EnvironmentSecretResolver(),
            new FileSecretResolver(),
            new ConfigurationSecretResolver(hostConfiguration),
        ]);

        // providers.llm[].kind picks the adapter for each entry.
        options.UseChatClients(new OpenAiChatClientAdapter());

        // providers.embeddings.kind picks the adapter.
        options.UseEmbeddings(new OpenAiEmbeddingGeneratorAdapter());

        // providers.knowledge.kind picks the adapter.
        options.UseKnowledgeStores(new QdrantKnowledgeAdapter());

        // providers.moderation.kind picks the adapter.
        options.UseModeration(new OpenAiModerationAdapter(httpClients));

        // providers.telemetry.kind picks the adapter.
        options.UseTelemetry(new GrafanaOtlpTelemetryAdapter());

        // providers.call.kind picks one.
        options.UseCall(new TelnyxRelayCallAdapter());

        // The speech vendor.
        options.UseSpeech(new TelnyxRelaySpeechAdapter());

        // kind: http. Every header resolved at startup, so no tool call costs a lookup.
        options.AddToolSource(startup =>
            new HttpToolSource(httpClients.CreateClient(HttpToolSource.HttpClientName), startup.Secrets));

        // mcp:. headers: and env: resolved at startup like every other credential.
        options.AddToolSource(startup => new McpToolSource(
            startup.Secrets, () => McpHttpClient(httpClients), loggers));

        // providers.audit.kind picks the adapter.
        options.UseAuditSinks(new PostgresAuditSinkAdapter());

        // providers.transcript.kind picks the adapter.
        options.UseTranscriptStores(new PostgresTranscriptStoreAdapter());
    }

    /// <summary>Opens the client a <c>transport: http</c> MCP server is reached on.</summary>
    /// <param name="pipeline">The one outbound pipeline every adapter shares.</param>
    /// <returns>The client. The pipeline owns the handler under it, so nothing here disposes it.</returns>
    internal static HttpClient McpHttpClient(AgentCoreHttpClients pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);

        return new HttpClient(pipeline.CreateHandler(McpHttpClientName), disposeHandler: false)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    /// <summary>Settles what only the last word can decide, once every seam has been written.</summary>
    /// <param name="options">The options every configure callback has now filled.</param>
    private static void FinishConfiguring(AgentCoreOptions options)
    {
        if (options.Configuration is not null)
        {
            options.ConfigurationPath = null;
        }

        AddCreateCaseStub(options);
        ApplyKnowledgeQueryAnalyzers(options);
        ApplyKnowledgePointMappers(options);
    }

    /// <summary>
    /// Hands a host's <see cref="AgentCoreOptions.UseKnowledgeQueryAnalyzers"/> call to the
    /// <see cref="QdrantKnowledgeAdapter"/> the defaults registered.
    /// </summary>
    private static void ApplyKnowledgeQueryAnalyzers(AgentCoreOptions options)
    {
        if (options.KnowledgeAnalyzers.Count == 0)
        {
            return;
        }

        if (options.KnowledgeStores?.OfType<QdrantKnowledgeAdapter>().FirstOrDefault() is { } knowledge)
        {
            knowledge.UseAnalyzers([.. options.KnowledgeAnalyzers]);
        }
    }

    /// <summary>
    /// Hands a host's <see cref="AgentCoreOptions.UseKnowledgePointMappers"/> call to the
    /// <see cref="QdrantKnowledgeAdapter"/> the defaults registered. Same post-configure timing
    /// rationale as <see cref="ApplyKnowledgeQueryAnalyzers"/>.
    /// </summary>
    private static void ApplyKnowledgePointMappers(AgentCoreOptions options)
    {
        if (options.KnowledgeMappers.Count == 0)
        {
            return;
        }

        if (options.KnowledgeStores?.OfType<QdrantKnowledgeAdapter>().FirstOrDefault() is { } knowledge)
        {
            knowledge.UseMappers([.. options.KnowledgeMappers]);
        }
    }

    /// <summary>Registers the example document's binding, when the host registered none.</summary>
    /// <param name="options">The options to bind the name on.</param>
    private static void AddCreateCaseStub(AgentCoreOptions options)
    {
        if (options.Bindings.Names.Contains(CreateCaseBinding, StringComparer.Ordinal))
        {
            return;
        }

        options.Bind(CreateCaseBinding, (arguments, _) => ValueTask.FromResult<object?>(new JsonObject
        {
            ["opened"] = false,
            ["summary"] = arguments["summary"]?.DeepClone(),
            ["reason"] = "this host has no case system bound. Register a CreateCase delegate that opens one.",
        }));
    }
}
