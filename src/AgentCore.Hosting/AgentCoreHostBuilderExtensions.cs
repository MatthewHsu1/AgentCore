using System.Text.Json.Nodes;
using AgentCore.AspNetCore.DependencyInjection;
using AgentCore.AspNetCore.Vendors.TelnyxRelay;
using AgentCore.Infrastructure.Evaluation.OpenAiModeration;
using AgentCore.Infrastructure.Knowledge.FileStore;
using AgentCore.Infrastructure.Knowledge.VectorData.Zilliz;
using AgentCore.Infrastructure.Llm.OpenAI;
using AgentCore.Infrastructure.Secrets;
using AgentCore.Infrastructure.Telemetry.Grafana;
using AgentCore.Infrastructure.Tools;
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

    /// <summary>Loads the document, binds every vendor seam, and registers the call services.</summary>
    /// <param name="builder">The host being built.</param>
    /// <param name="configure">
    /// The host's own word on the options, run after the defaults below and therefore winning over
    /// every one of them. A host uses it to bind a <c>kind: binding</c> delegate, to add a vendor
    /// this library does not name, or to replace the document, the secret resolver, the logger
    /// factory, or any vendor seam.
    /// </param>
    /// <param name="cancellationToken">Cancels the start: the secret reads and the adapter builds.</param>
    /// <returns>The same builder, so a host chains its calls.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
    public static async ValueTask<WebApplicationBuilder> AddAgentCoreHostAsync(
        this WebApplicationBuilder builder,
        Action<AgentCoreOptions>? configure = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var loggers = LoggerFactory.Create(logging => logging
            .AddConfiguration(builder.Configuration.GetSection("Logging"))
            .AddConsole());

        builder.Services.AddSingleton(loggers);

        // One outbound HTTP pipeline for the life of the process.
        AgentCoreHttpClients httpClients = new(loggers: loggers);
        builder.Services.AddSingleton(httpClients);

        await builder.Services.AddAgentCoreAsync(
            options => Configure(builder, options, httpClients, loggers, configure),
            cancellationToken).ConfigureAwait(false);

        // The relay socket is the inbound path of a real call, and a dead peer must not hold a
        // session for the shipped two-minute default. This sets the twenty-second keep-alive
        // numbers a call needs.
        builder.Services.AddAgentCoreWebSockets();

        return builder;
    }

    /// <summary>Fills the options: this library's defaults first, then the host's word.</summary>
    /// <param name="builder">The host being built, read for the document path.</param>
    /// <param name="options">The options <see cref="AgentCoreServiceCollectionExtensions.AddAgentCoreAsync"/> passed in.</param>
    /// <param name="httpClients">The one outbound pipeline every adapter shares.</param>
    /// <param name="loggers">The factory built before the container exists.</param>
    /// <param name="configure">The host's own word, or <see langword="null"/>.</param>
    private static void Configure(
        WebApplicationBuilder builder,
        AgentCoreOptions options,
        AgentCoreHttpClients httpClients,
        ILoggerFactory loggers,
        Action<AgentCoreOptions>? configure)
    {
        options.ConfigurationPath =
            builder.Configuration[ConfigurationPathKey] ?? DefaultConfigurationPath;

        options.SecretResolver = new ChainedSecretResolver(
            [new EnvironmentSecretResolver(), new FileSecretResolver()]);

        options.LoggerFactory = loggers;

        // providers.llm[].kind picks the adapter for each entry.
        options.UseChatClients(new OpenAiChatClientAdapter());

        // providers.knowledge.search and providers.knowledge.documents pick the adapter for each
        // port.
        options.UseKnowledgeStores(new FileSystemKnowledgeAdapter(), new ZillizKnowledgeAdapter(httpClients));

        // providers.moderation.kind picks the adapter.
        options.UseModeration(new OpenAiModerationAdapter(httpClients));

        // providers.telemetry.kind picks the adapter.
        options.UseTelemetry(new GrafanaOtlpTelemetryAdapter());

        // providers.call.kind picks one.
        options.UseCall(new TelnyxRelayCallAdapter());

        // The speech vendor.
        options.UseSpeech(new TelnyxRelaySpeechAdapter());

        // kind: http. Every header resolved at startup, so no tool call costs a lookup.
        options.AddToolFactory(startup =>
            new HttpToolFactory(httpClients.CreateClient(HttpToolFactory.HttpClientName), startup.Secrets));

        // D23 puts the audit chain in PostgreSQL, and that adapter is not written. No audit vendor is
        // named here at all, so providers.audit falls back to the in-process memory sink: chain_check
        // has something to read and no event is silently lost. The fallback announces itself with a
        // warning at startup, because it is not durable. When the PostgreSQL adapter is written it is
        // listed here, once, exactly as the seams above, and providers.audit.kind picks it.

        // The host has the last word, and it has to be the last word: every Use* seam above is a
        // setter and not a list, so whoever writes last wins. A host that binds its own chat client
        // factory, or its own moderation vendor, would lose it to the defaults if this ran earlier.
        configure?.Invoke(options);

        if (options.Configuration is not null)
        {
            options.ConfigurationPath = null;
        }

        AddCreateCaseStub(options);
    }

    /// <summary>Registers the example document's binding, when the host registered none.</summary>
    /// <param name="options">The options to bind the name on.</param>
    /// <remarks>
    /// This is scaffolding for the shipped example document and not a feature. Replace it by binding
    /// <see cref="CreateCaseBinding"/> in the <c>configure</c> callback, or drop it by pointing
    /// <see cref="ConfigurationPathKey"/> at a document that declares no <c>create_case</c> tool.
    /// </remarks>
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
