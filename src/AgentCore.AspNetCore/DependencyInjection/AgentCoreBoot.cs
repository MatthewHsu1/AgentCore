using AgentCore.Application.Audit;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Evaluation;
using AgentCore.Application.Knowledge;
using AgentCore.Application.Ports;
using AgentCore.Application.Secrets;
using AgentCore.Application.Tools.Binding;
using AgentCore.Application.Tools.Registry;
using AgentCore.AspNetCore.Call;
using AgentCore.AspNetCore.DependencyInjection.Startup;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentCore.AspNetCore.DependencyInjection;

/// <summary>Everything one document opens, behind one owner the container built.</summary>
internal sealed class AgentCoreBoot : IAsyncDisposable, IDisposable
{
    private readonly AgentCoreOptions _options;

    private readonly ILoggerFactory _loggers;

    private readonly IServiceProvider? _services;

    private readonly List<object> _opened = [];

    private readonly Lock _gate = new();

    private BootState? _state;

    private int _closed;
    
    /// <summary>Takes the options a host filled and the loggers the container holds.</summary>
    /// <param name="options">The options every <c>Use*</c> seam wrote into.</param>
    /// <param name="loggers">The container's factory, used unless the options name another.</param>
    /// <param name="services">The container, read for the mapped routes the startup check walks.</param>
    public AgentCoreBoot(IOptions<AgentCoreOptions> options, ILoggerFactory loggers, IServiceProvider? services = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loggers);

        _options = options.Value;
        _loggers = _options.LoggerFactory ?? loggers;
        _services = services;
    }

    /// <summary>Gets the loaded document.</summary>
    internal AgentCoreConfiguration Configuration => Started.Configuration;

    /// <summary>Gets every <c>${secret:name}</c> value, read once while the host started.</summary>
    internal ResolvedSecrets Secrets => Started.Secrets;

    /// <summary>Gets the bindings the host registered by name.</summary>
    internal ToolBindingRegistry Bindings => _options.Bindings;

    /// <summary>Gets the registry that compiled the document, and would compile it again.</summary>
    internal CompiledAgentRegistry CompiledRegistry => Started.Graph.Registry;

    /// <summary>Gets the compiled entries, keyed by entry name. Every call shares them.</summary>
    internal IReadOnlyDictionary<string, CompiledAgent> CompiledEntries => Started.Graph.Entries;

    /// <summary>Gets the factory the compile table asks for every agent and for the extractor.</summary>
    internal IChatClientFactory ChatClients => Started.Graph.ChatClients;

    /// <summary>Gets the shared guard evaluator.</summary>
    internal IGuardEvaluator Guards => Started.Graph.Guards;

    /// <summary>Gets the registry the compile table reads.</summary>
    internal ToolRegistry Tools => Started.Tools;

    /// <summary>Gets the backing every call's row and every word of it is kept in.</summary>
    internal ICallStore Calls => Started.Calls;

    /// <summary>Gets the registry the turn loop reads, and the offline golden set alike.</summary>
    internal EvaluatorRegistry Evaluators => Started.Evaluators;

    /// <summary>Gets the queue that answers the audit port, not the store behind it.</summary>
    internal QueuedAuditSink AuditQueue => Started.AuditQueue;

    /// <summary>Gets one factory, one agent, and one session store per entry.</summary>
    internal EntryRegistry Entries => Started.Entries;

    /// <summary>Gets the container, read for host-registered seams the boot honors.</summary>
    internal IServiceProvider? Services => _services;

    /// <summary>Gets the knowledge base, or <see langword="null"/> when no agent reads one.</summary>
    internal IKnowledgeRetrievalPort? Knowledge => Started.Knowledge;

    /// <summary>Gets what each entry's call route runs, keyed by entry name, or <see langword="null"/> when no call routes here.</summary>
    internal IReadOnlyDictionary<string, RequestDelegate>? CallHandlers => Started.CallHandlers;

    /// <summary>Gets why no call routes here, or <see langword="null"/> when calls route.</summary>
    internal string? CallUnroutable => Started.CallUnroutable;

    /// <summary>Gets the call transports the host registered, or <see langword="null"/> if it registered none.</summary>
    internal IReadOnlyList<ICallAdapter>? CallAdapters => Started.CallAdapters;

    /// <summary>Gets the speech vendors the host registered, or <see langword="null"/> if it registered none.</summary>
    internal IReadOnlyList<ISpeechAdapter>? SpeechAdapters => Started.SpeechAdapters;

    /// <summary>Gets the telemetry export, or <see langword="null"/> when the host registered no vendor.</summary>
    internal ITelemetrySession? Telemetry => Started.Telemetry;

    private BootState Started => _state ?? throw NotStarted();

    /// <summary>Takes ownership of one resource, and hands it straight back.</summary>
    /// <typeparam name="T">The resource's own type, so a caller loses nothing by owning it.</typeparam>
    /// <param name="resource">What to close when the host stops. Anything not disposable is ignored.</param>
    /// <returns><paramref name="resource"/>, unchanged.</returns>
    internal T Track<T>(T resource)
    {
        if (resource is IAsyncDisposable or IDisposable)
        {
            lock (_gate)
            {
                _opened.Add(resource);
            }
        }

        return resource;
    }

    /// <summary>Gives up ownership of one resource, and hands it straight back.</summary>
    /// <typeparam name="T">What the caller is resolving.</typeparam>
    /// <param name="resource">What the container is about to take.</param>
    /// <returns><paramref name="resource"/>, unchanged.</returns>
    internal T Release<T>(T resource)
    {
        if (resource is IAsyncDisposable or IDisposable)
        {
            lock (_gate)
            {
                _opened.Remove(resource);
            }
        }

        return resource;
    }

    /// <summary>Loads the document, opens everything it names, and compiles it.</summary>
    /// <param name="cancellationToken">Cancels the secret reads and the adapter builds.</param>
    /// <returns>A task that completes when the graph is ready to take a call.</returns>
    /// <exception cref="InvalidOperationException">
    /// The options name no document, name two, or bind no chat client adapter.
    /// </exception>
    /// <exception cref="ConfigurationLoadException">
    /// The document fails one of the eight checks, names a <c>kind</c> no registered adapter serves,
    /// does not compile, or a mapped route names an entry it does not declare.
    /// </exception>
    /// <exception cref="SecretResolutionException">One <c>${secret:name}</c> reference resolves to nothing.</exception>
    internal async ValueTask BootAsync(CancellationToken cancellationToken)
    {
        var (configuration, configurationWarnings) = ConfigurationStartup.Load(_options);

        var telemetry = Track(await TelemetryStartup
            .StartAsync(configuration, _options, _loggers, cancellationToken)
            .ConfigureAwait(false));

        var bootLogger = _loggers.CreateLogger<AgentCoreBoot>();
        foreach (var warning in configurationWarnings)
        {
            AgentCoreBootLog.ConfigurationWarning(bootLogger, warning.ToString());
        }

        var secrets = await SecretsStartup
            .ResolveAsync(configuration, _options, cancellationToken)
            .ConfigureAwait(false);

        AgentCoreStartup startup = new(configuration, secrets);

        var agents = configuration.Agents;

        var embeddings = Track(await EmbeddingStartup
            .OpenAsync(configuration, _options, cancellationToken)
            .ConfigureAwait(false));

        var knowledge = Track(await KnowledgeStartup
            .OpenAsync(
                configuration,
                _options,
                startup,
                embeddings,
                scopeDeclared: AgentKnowledge.AnyScoped(agents),
                requireScope: AgentKnowledge.AllScoped(agents),
                cancellationToken)
            .ConfigureAwait(false));

        var chatClients = Track(await ChatClientStartup
            .BuildAsync(_options, startup, cancellationToken)
            .ConfigureAwait(false));

        var tools = await ToolRegistryStartup
            .BuildAsync(this, _options, startup, chatClients, configuration, cancellationToken)
            .ConfigureAwait(false);

        ConfigurationValidator.ValidateToolReferences(configuration, tools.ServedIds);

        var skills = await SkillsStartup
            .OpenAsync(_options, _loggers, cancellationToken)
            .ConfigureAwait(false);

        if (skills is not null)
        {
            Track(skills.Source);
            ConfigurationValidator.ValidateSkillReferences(configuration, skills.Names);
        }

        ConfigurationValidator.ValidateSkillToolNames(configuration);

        var calls = Track(await CallStartup
            .OpenAsync(configuration, _options, _loggers, cancellationToken)
            .ConfigureAwait(false));

        var evaluators = await EvaluationStartup
            .CreateRegistryAsync(configuration, _options, cancellationToken)
            .ConfigureAwait(false);

        var graph = await CompilationStartup
            .CompileAsync(
                configuration,
                chatClients,
                tools.Registry,
                calls,
                evaluators,
                knowledge,
                skills,
                KnowledgeCitationFormatterFactory.Resolve(configuration, _options.KnowledgeCitations),
                _loggers,
                _options.WorkspaceRoot)
            .ConfigureAwait(false);

        var seams = CallSeamStartup.Build(configuration, _options);

        var call = await CallSessionStartup
            .OpenAsync(this, configuration, _options, graph, _loggers, cancellationToken)
            .ConfigureAwait(false);

        _state = new BootState(
            configuration,
            secrets,
            telemetry,
            tools.Registry,
            calls,
            evaluators,
            graph,
            call.Entries,
            call.Queue,
            knowledge,
            seams.Call,
            seams.Speech,
            seams.Handlers,
            seams.Unroutable);

        ValidateMappedEntries(configuration);
    }

    /// <summary>Refuses a mapped route that names an entry the document does not declare.</summary>
    /// <param name="configuration">The loaded document. It carries the declared entries.</param>
    /// <exception cref="ConfigurationLoadException">A route names an unknown entry.</exception>
    private void ValidateMappedEntries(AgentCoreConfiguration configuration)
    {
        var sources = _services?.GetService<IEnumerable<EndpointDataSource>>();
        if (sources is null)
        {
            return;
        }

        List<ConfigurationError> failures = [];
        foreach (var endpoint in sources.SelectMany(source => source.Endpoints))
        {
            if (endpoint.Metadata.GetMetadata<AgentCoreEntryMetadata>() is not { } mapped)
            {
                continue;
            }

            if (!configuration.Entries.ContainsKey(mapped.Entry))
            {
                failures.Add(new ConfigurationError
                {
                    Pointer = "/entries",
                    Message =
                        $"Map{mapped.Surface} names an unknown entry. "
                        + EntryRegistry.UnknownEntryMessage(mapped.Entry, configuration.Entries.Keys),
                    Check = ConfigurationCheck.ReferenceResolution,
                });
            }
        }

        if (failures.Count > 0)
        {
            throw new ConfigurationLoadException(failures);
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync() => await CloseAsync().ConfigureAwait(false);

    /// <summary>Closes everything, blocking until it is done.</summary>
    public void Dispose() => CloseAsync().AsTask().GetAwaiter().GetResult();

    private async ValueTask CloseAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        object[] opened;

        lock (_gate)
        {
            opened = [.. _opened];
            _opened.Clear();
        }

        for (var index = opened.Length - 1; index >= 0; index--)
        {
            try
            {
                switch (opened[index])
                {
                    // Asynchronous first: a resource that carries both, as the audit queue does, must
                    // not be drained on the path that blocks a thread while it waits.
                    case IAsyncDisposable asyncDisposable:
                        await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                        break;

                    case IDisposable disposable:
                        disposable.Dispose();
                        break;
                }
            }
            catch
            {
                // A boot that already failed carries the exception a deployer needs; a resource that
                // also fails to close must not replace or hide it.
            }
        }
    }

    private static InvalidOperationException NotStarted()
        => new(
            "AgentCore has not booted: the document is loaded, and every adapter it names is opened, "
            + "when the host starts. Resolve this service from a started host — await "
            + "host.StartAsync(), or app.Run() — and not from a provider nobody started.");

    private sealed record BootState(
        AgentCoreConfiguration Configuration,
        ResolvedSecrets Secrets,
        ITelemetrySession? Telemetry,
        ToolRegistry Tools,
        ICallStore Calls,
        EvaluatorRegistry Evaluators,
        CompiledGraph Graph,
        EntryRegistry Entries,
        QueuedAuditSink AuditQueue,
        IKnowledgeRetrievalPort? Knowledge,
        IReadOnlyList<ICallAdapter>? CallAdapters,
        IReadOnlyList<ISpeechAdapter>? SpeechAdapters,
        IReadOnlyDictionary<string, RequestDelegate>? CallHandlers,
        string? CallUnroutable);
}

/// <summary>Every line <see cref="AgentCoreBoot"/> writes itself, below what each startup step logs.</summary>
internal static partial class AgentCoreBootLog
{
    /// <summary>One of section 8.5's structural warnings, or one of section 10's two (K33, K39).</summary>
    /// <param name="logger">The boot's own logger.</param>
    /// <param name="warning">The warning's <c>ToString()</c>: its pointer, its message, and its check.</param>
    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "{Warning}")]
    public static partial void ConfigurationWarning(ILogger logger, string warning);
}
