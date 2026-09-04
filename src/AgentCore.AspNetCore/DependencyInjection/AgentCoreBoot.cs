using AgentCore.Application.Audit;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Evaluation;
using AgentCore.Application.Knowledge;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Secrets;
using AgentCore.Application.State;
using AgentCore.Application.Tools.Binding;
using AgentCore.Application.Tools.Registry;
using AgentCore.AspNetCore.DependencyInjection.Startup;
using AgentCore.AspNetCore.Sessions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentCore.AspNetCore.DependencyInjection;

/// <summary>Everything one document opens, behind one owner the container built.</summary>
internal sealed class AgentCoreBoot : IAsyncDisposable, IDisposable
{
    private readonly AgentCoreOptions _options;

    private readonly ILoggerFactory _loggers;

    private readonly List<object> _opened = [];

    private readonly Lock _gate = new();

    private BootState? _state;

    private int _closed;

    /// <summary>Takes the options a host filled and the loggers the container holds.</summary>
    /// <param name="options">The options every <c>Use*</c> seam wrote into.</param>
    /// <param name="loggers">The container's factory, used unless the options name another.</param>
    public AgentCoreBoot(IOptions<AgentCoreOptions> options, ILoggerFactory loggers)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loggers);

        _options = options.Value;
        _loggers = _options.LoggerFactory ?? loggers;
    }

    /// <summary>Gets the loaded document.</summary>
    internal AgentCoreConfiguration Configuration => Started.Configuration;

    /// <summary>Gets every <c>${secret:name}</c> value, read once while the host started.</summary>
    internal ResolvedSecrets Secrets => Started.Secrets;

    /// <summary>Gets the bindings the host registered by name.</summary>
    /// <remarks>
    /// Readable before the boot runs: a host filled it, so no document had to be loaded for it to
    /// hold what it holds.
    /// </remarks>
    internal ToolBindingRegistry Bindings => _options.Bindings;

    /// <summary>Gets the registry that compiled the document, and would compile it again.</summary>
    internal CompiledAgentRegistry CompiledRegistry => Started.Graph.Registry;

    /// <summary>Gets the one graph every call shares.</summary>
    internal CompiledAgent Compiled => Started.Graph.Compiled;

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

    /// <summary>Gets the factory that builds one session per call.</summary>
    internal ICallSessionFactory Sessions => Started.Sessions;

    /// <summary>Gets the same turn loop, behind the framework's own agent seam.</summary>
    internal AgentCoreAgent Agent => Started.Agent;

    /// <summary>Gets the call transports the host registered, or <see langword="null"/> if it registered none.</summary>
    internal IReadOnlyList<ICallAdapter>? CallAdapters => Started.CallAdapters;

    /// <summary>Gets the speech vendors the host registered, or <see langword="null"/> if it registered none.</summary>
    internal IReadOnlyList<ISpeechAdapter>? SpeechAdapters => Started.SpeechAdapters;

    /// <summary>Gets the telemetry export, or <see langword="null"/> when the host registered no vendor.</summary>
    internal ITelemetrySession? Telemetry => Started.Telemetry;

    /// <summary>Gets what the call route runs, or <see langword="null"/> when no call routes here.</summary>
    internal RequestDelegate? CallHandler => Started.CallHandler;

    /// <summary>Gets why no call routes here, or <see langword="null"/> when one does.</summary>
    internal string? CallUnroutable => Started.CallUnroutable;

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
    /// or does not compile.
    /// </exception>
    /// <exception cref="SecretResolutionException">One <c>${secret:name}</c> reference resolves to nothing.</exception>
    internal async ValueTask BootAsync(CancellationToken cancellationToken)
    {
        var (configuration, configurationWarnings) = ConfigurationStartup.Load(_options);

        HashSet<string> registeredLinkers = new(StringComparer.Ordinal) { SlotVocabularyConfiguration.DefaultLinker };
        registeredLinkers.UnionWith(_options.StateValueLinkers.Select(static linker => linker.Name));
        ConfigurationValidator.ValidateLinkerNames(configuration, registeredLinkers);

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

        var agents = configuration.Agents ?? new AgentsConfiguration { Items = [] };

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

        VocabularyCache vocabulary = new(_options.TimeProvider);

        await KnowledgeStartup
            .ApplyVocabularyAsync(
                configuration, knowledge, vocabulary, _loggers.CreateLogger(typeof(KnowledgeStartup)), cancellationToken)
            .ConfigureAwait(false);

        StartVocabularyRefresh(configuration, knowledge, vocabulary, cancellationToken);

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
                _loggers)
            .ConfigureAwait(false);

        var seams = CallSeamStartup.Build(configuration, _options);

        var call = await CallSessionStartup
            .OpenAsync(this, configuration, _options, graph, vocabulary, _loggers, cancellationToken)
            .ConfigureAwait(false);

        _state = new BootState(
            configuration,
            secrets,
            telemetry,
            tools.Registry,
            calls,
            evaluators,
            graph,
            call.Sessions,
            call.Agent,
            call.Queue,
            seams.Call,
            seams.Speech,
            seams.Handler,
            seams.Unroutable);
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

    /// <summary>
    /// Starts one <see cref="VocabularyRefreshService"/> per <c>vocabulary:</c> slot that declares a
    /// <c>refreshSeconds</c> above zero. <c>0</c> means boot only (§4), so those slots start none.
    /// </summary>
    /// <param name="configuration">The loaded document.</param>
    /// <param name="knowledge">The port <see cref="KnowledgeStartup.OpenAsync"/> built, or <see langword="null"/>.</param>
    /// <param name="vocabulary">The cache every refresh installs into — the same one every call session reads.</param>
    /// <param name="cancellationToken">Passed to each service's own <c>StartAsync</c>.</param>
    private void StartVocabularyRefresh(
        AgentCoreConfiguration configuration,
        IKnowledgeRetrievalPort? knowledge,
        VocabularyCache vocabulary,
        CancellationToken cancellationToken)
    {
        if (knowledge is not IFacetVocabularyPort port)
        {
            return;
        }

        var scope = configuration.Providers?.Knowledge?.Scope;
        if (scope is null || ScopeTemplate.Parse(scope.Template) is not { } template)
        {
            return;
        }

        var wildcard = scope.Wildcard;

        foreach (var (slotName, slot) in configuration.State)
        {
            if (slot.Vocabulary is not { RefreshSeconds: > 0 } vocab)
            {
                continue;
            }

            var path = template.Resolve(slotName);
            var wildcardValue = wildcard is not null && wildcard.Facets.Contains(slotName, StringComparer.Ordinal)
                ? wildcard.Value
                : null;

            var service = Track(new VocabularyRefreshService(
                slotName,
                path,
                vocab.MaxValues,
                wildcardValue,
                vocab.RefreshSeconds,
                port,
                vocabulary,
                _options.TimeProvider ?? TimeProvider.System,
                _loggers.CreateLogger<VocabularyRefreshService>()));

            // BackgroundService.StartAsync only arms the loop — it runs ExecuteAsync through
            // Task.Run and returns immediately, so awaiting it here would not wait for a single
            // refresh, only for the arm itself.
            _ = service.StartAsync(cancellationToken);
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
        ICallSessionFactory Sessions,
        AgentCoreAgent Agent,
        QueuedAuditSink AuditQueue,
        IReadOnlyList<ICallAdapter>? CallAdapters,
        IReadOnlyList<ISpeechAdapter>? SpeechAdapters,
        RequestDelegate? CallHandler,
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
