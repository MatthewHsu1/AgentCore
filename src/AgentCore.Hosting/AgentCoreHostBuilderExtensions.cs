using System.Text.Json.Nodes;
using AgentCore.AspNetCore.DependencyInjection;
using AgentCore.AspNetCore.Vendors.OpenAiLive;
using AgentCore.AspNetCore.Vendors.TelnyxRelay;
using AgentCore.Hosting.Secrets;
using AgentCore.Infrastructure.Audit.Postgres;
using AgentCore.Infrastructure.Blobs.S3;
using AgentCore.Infrastructure.Conversation.Postgres;
using AgentCore.Infrastructure.Embeddings.OpenAI;
using AgentCore.Infrastructure.Evaluation.OpenAiModeration;
using AgentCore.Infrastructure.Knowledge.VectorData.Qdrant;
using AgentCore.Infrastructure.Llm.ModelsDev;
using AgentCore.Infrastructure.Llm.OpenAI;
using AgentCore.Infrastructure.Llm.OpenCodeGo;
using AgentCore.Infrastructure.Secrets;
using AgentCore.Infrastructure.Telemetry.Grafana;
using AgentCore.Infrastructure.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentCore.Hosting
{
    /// <summary>
    /// Registers everything AgentCore needs to run, in one conversation.
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
        /// <returns>The same builder, so a host chains its conversations.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        public static WebApplicationBuilder AddAgentCoreHost(
            this WebApplicationBuilder builder,
            Action<AgentCoreOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(builder);

            _ = builder.Logging.AddConsole();

            _ = builder.Services.AddSingleton(provider => new AgentCoreHttpClients(
                loggers: provider.GetRequiredService<ILoggerFactory>()));

            _ = builder.Services
                .AddOptions<AgentCoreOptions>()
                .Configure<AgentCoreHttpClients, IConfiguration, ILoggerFactory>(
                    (options, httpClients, hostConfiguration, loggers) =>
                        Configure(hostConfiguration, options, httpClients, loggers));

            _ = builder.Services.AddAgentCore(options => configure?.Invoke(options));

            _ = builder.Services.PostConfigure<AgentCoreOptions>(FinishConfiguring);

            _ = builder.Services.AddAgentCoreWebSockets();

            return builder;
        }

        /// <summary>Fills the options with this library's defaults.</summary>
        /// <param name="hostConfiguration">The host's own configuration, read for the document path and for secrets.</param>
        /// <param name="options">The options <see cref="AgentCoreServiceCollectionExtensions.AddAgentCore"/> registered.</param>
        /// <param name="httpClients">The one outbound pipeline every adapter shares.</param>
        /// <param name="loggers">The host's own logging, for the adapters that report what they are doing.</param>
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

            // Every vendor's models.dev lookup shares one fetch of the catalog.
            _ = options.UseModelCatalog(ModelsDevCatalogPort.CreateFromConfiguration(httpClients, hostConfiguration));

            _ = options.UseChatClients(
                new OpenAiChatClientAdapter(),
                OpenCodeGoChatClientAdapter.CreateFromConfiguration(httpClients, hostConfiguration));

            _ = options.UseEmbeddings(new OpenAiEmbeddingGeneratorAdapter());

            _ = options.UseKnowledgeStores(new QdrantKnowledgeAdapter());

            _ = options.UseModeration(new OpenAiModerationAdapter(httpClients));

            _ = options.UseTelemetry(new GrafanaOtlpTelemetryAdapter());

            // The host's own callback runs after this one and may set another resolver, so each adapter reads it late.
            _ = options.UseConversation(
                new TelnyxRelayConversationAdapter(() => options.SecretResolver),
                new OpenAiLiveConversationAdapter(httpClients, () => options.SecretResolver));

            _ = options.UseSpeech(new TelnyxRelaySpeechAdapter());

            // kind: http. Every header resolved at startup, so no tool call costs a lookup.
            _ = options.AddToolSource(startup =>
                new HttpToolSource(httpClients.CreateClient(HttpToolSource.HttpClientName), startup.Secrets));

            // mcp:. headers: and env: resolved at startup like every other credential.
            _ = options.AddToolSource(startup => new McpToolSource(
                startup.Secrets, () => McpHttpClient(httpClients), loggers));

            _ = options.UseAuditSinks(new PostgresAuditSinkAdapter());

            _ = options.UseConversationStores(new PostgresConversationStoreAdapter());

            _ = options.UseBlobStores(new S3BlobStoreAdapter());
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
                _ = knowledge.UseAnalyzers([.. options.KnowledgeAnalyzers]);
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
                _ = knowledge.UseMappers([.. options.KnowledgeMappers]);
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

            _ = options.Bind(CreateCaseBinding, (arguments, _) => ValueTask.FromResult<object?>(new JsonObject
            {
                ["opened"] = false,
                ["summary"] = arguments["summary"]?.DeepClone(),
                ["reason"] = "this host has no case system bound. Register a CreateCase delegate that opens one.",
            }));
        }
    }
}
