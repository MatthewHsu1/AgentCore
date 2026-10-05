using System.Diagnostics.CodeAnalysis;
using AgentCore.Application.Audit;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Conversation;
using AgentCore.Application.Evaluation;
using AgentCore.Application.Hooks;
using AgentCore.Application.Hooks.Engine;
using AgentCore.Application.Knowledge;
using AgentCore.Application.Ports;
using AgentCore.Application.Secrets;
using AgentCore.Application.Skills;
using AgentCore.Application.Tools.Binding;
using AgentCore.Application.Tools.Builtin;
using AgentCore.Application.Tools.Registry;
using AgentCore.AspNetCore.DependencyInjection.Startup;
using AgentCore.AspNetCore.Voice.Ports;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentCore.AspNetCore.DependencyInjection
{
    /// <summary>Everything one document opens, behind one owner the container built.</summary>
    internal sealed class AgentCoreBoot : IAsyncDisposable, IDisposable
    {
        private readonly AgentCoreOptions _options;

        private readonly ILoggerFactory _loggers;

        private readonly List<object> _opened = [];

        private readonly Lock _gate = new();
        
        private int _closed;

        private BootState? _state;

        /// <summary>Takes the options a host filled and the loggers the container holds.</summary>
        /// <param name="options">The options every <c>Use*</c> seam wrote into.</param>
        /// <param name="loggers">The container's factory, used unless the options name another.</param>
        /// <param name="services">The container, read for host-registered seams the boot honors.</param>
        public AgentCoreBoot(IOptions<AgentCoreOptions> options, ILoggerFactory loggers, IServiceProvider? services = null)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(loggers);

            _options = options.Value;
            _loggers = _options.LoggerFactory ?? loggers;
            Services = services;
        }

        /// <summary>Gets the loaded document.</summary>
        internal AgentCoreConfiguration Configuration => Started.Configuration;

        /// <summary>Gets every <c>${secret:name}</c> value, read once while the host started.</summary>
        internal ResolvedSecrets Secrets => Started.Secrets;

        /// <summary>Gets the bindings the host registered by name.</summary>
        internal ToolBindingRegistry Bindings => _options.Bindings;

        /// <summary>Gets the registry that compiled the document, and would compile it again.</summary>
        internal CompiledAgentRegistry CompiledRegistry => Started.Graph.Registry;

        /// <summary>Gets the compiled entries, keyed by entry name. Every conversation shares them.</summary>
        internal IReadOnlyDictionary<string, CompiledAgent> CompiledEntries => Started.Graph.Entries;

        /// <summary>Gets the factory the compile table asks for every agent and for the extractor.</summary>
        internal IChatClientFactory ChatClients => Started.Graph.ChatClients;

        /// <summary>Gets the shared guard evaluator.</summary>
        internal IGuardEvaluator Guards => Started.Graph.Guards;

        /// <summary>Gets the registry the compile table reads.</summary>
        internal ToolRegistry Tools => Started.Tools;

        /// <summary>Gets the backing every conversation's row and every word of it is kept in.</summary>
        internal Conversations Conversations => Started.Conversations;

        /// <summary>Gets the registry the turn loop reads, and the offline golden set alike.</summary>
        internal EvaluatorRegistry Evaluators => Started.Evaluators;

        /// <summary>Gets the queue that answers the audit port, not the store behind it.</summary>
        internal QueuedAuditSink AuditQueue => Started.AuditQueue;

        /// <summary>Gets one factory and one agent per entry, over the one session owner shared by every entry.</summary>
        internal EntryRegistry Entries => Started.Entries;

        /// <summary>Gets the container, read for host-registered seams the boot honors.</summary>
        internal IServiceProvider? Services { get; }

        /// <summary>Gets the knowledge base, or <see langword="null"/> when no agent reads one.</summary>
        internal IKnowledgeRetrievalPort? Knowledge => Started.Knowledge;

        /// <summary>Gets the blob store, or <see langword="null"/> when the document names none.</summary>
        internal IBlobStore? Blobs => Started.Blobs;

        /// <summary>Gets what the conversation route runs, or <see langword="null"/> when no conversation routes here.</summary>
        internal ConversationRoute? ConversationRoute => Started.ConversationRoute;

        /// <summary>Gets why no conversation routes here, or <see langword="null"/> when conversations route.</summary>
        internal string? ConversationUnroutable => Started.ConversationUnroutable;

        /// <summary>Gets the conversation transports the host registered, or <see langword="null"/> if it registered none.</summary>
        internal IReadOnlyList<IConversationAdapter>? ConversationAdapters => Started.ConversationAdapters;

        /// <summary>Gets the speech vendors the host registered, or <see langword="null"/> if it registered none.</summary>
        internal IReadOnlyList<ISpeechAdapter>? SpeechAdapters => Started.SpeechAdapters;

        /// <summary>Gets the telemetry export, or <see langword="null"/> when the host registered no vendor.</summary>
        internal ITelemetrySession? Telemetry => Started.Telemetry;

        /// <summary>Gets the hooks every entry was compiled with.</summary>
        internal HookRuntime Hooks => Started.Hooks;

        private BootState Started { get => _state ?? throw NotStarted(); set => _state = value; }

        /// <summary>Gets the hooks, or <see langword="false"/> when the boot never finished: a stop after a failed boot.</summary>
        internal bool TryGetHooks([NotNullWhen(true)] out HookRuntime? hooks) => (hooks = _state?.Hooks) is not null;

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
                    _ = _opened.Remove(resource);
                }
            }

            return resource;
        }

        /// <summary>Loads the document, opens everything it names, and compiles it.</summary>
        /// <param name="cancellationToken">Cancels the secret reads and the adapter builds.</param>
        /// <returns>A task that completes when the graph is ready to take a conversation.</returns>
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
            (AgentCoreConfiguration? configuration, IReadOnlyList<ConfigurationError>? configurationWarnings) = ConfigurationStartup.Load(_options);

            ITelemetrySession? telemetry = Track(await TelemetryStartup
                .StartAsync(configuration, _options, _loggers, cancellationToken)
                .ConfigureAwait(false));

            ILogger<AgentCoreBoot> bootLogger = _loggers.CreateLogger<AgentCoreBoot>();
            foreach (ConfigurationError warning in configurationWarnings)
            {
                AgentCoreBootLog.ConfigurationWarning(bootLogger, warning.ToString());
            }

            ResolvedSecrets secrets = await SecretsStartup
                .ResolveAsync(configuration, _options, cancellationToken)
                .ConfigureAwait(false);

            AgentCoreStartup startup = new(configuration, secrets);

            AgentsConfiguration agents = configuration.Agents;

            IEmbeddingGenerator<string, Embedding<float>>? embeddings = Track(await EmbeddingStartup
                .OpenAsync(configuration, _options, cancellationToken)
                .ConfigureAwait(false));

            IKnowledgeRetrievalPort? knowledge = Track(await KnowledgeStartup
                .OpenAsync(
                    configuration,
                    _options,
                    startup,
                    embeddings,
                    scopeDeclared: AgentKnowledge.AnyScoped(agents),
                    requireScope: AgentKnowledge.AllScoped(agents),
                    cancellationToken)
                .ConfigureAwait(false));

            IChatClientFactory chatClients = Track(await ChatClientStartup
                .BuildAsync(_options, startup, cancellationToken)
                .ConfigureAwait(false));

            // Opened before the tools: file.publish reads the store at build time.
            IBlobStore? blobs = Track(await BlobStartup
                .OpenAsync(configuration, _options, cancellationToken)
                .ConfigureAwait(false));

            ToolRegistryBuildResult tools = await ToolRegistryStartup
                .BuildAsync(
                    this,
                    _options,
                    startup,
                    new BuiltinToolPorts(chatClients, blobs, WorkspaceRoot: _options.WorkspaceRoot, Loggers: _loggers),
                    cancellationToken)
                .ConfigureAwait(false);

            ConfigurationValidator.ValidateToolReferences(configuration, tools.ServedIds);
            FillerKeyCheck.Warn(configuration, tools.ServedIds, bootLogger);

            SkillCatalog? skills = await SkillsStartup
                .OpenAsync(_options, _loggers, cancellationToken)
                .ConfigureAwait(false);

            if (skills is not null)
            {
                _ = Track(skills.Source);
                ConfigurationValidator.ValidateSkillReferences(configuration, skills.Names);
            }

            ConfigurationValidator.ValidateSkillToolNames(configuration);

            IConversationStore store = Track(await ConversationStartup
                .OpenAsync(configuration, _options, _loggers, cancellationToken)
                .ConfigureAwait(false));

            Conversations conversations = new(store, blobs);

            EvaluatorRegistry evaluators = await EvaluationStartup
                .CreateRegistryAsync(configuration, _options, cancellationToken)
                .ConfigureAwait(false);

            (IReadOnlyList<AgentHook> hooks, QueuedAuditSink auditQueue) = await HookStartup
                .OpenAsync(this, configuration, _options, _loggers, Services, cancellationToken)
                .ConfigureAwait(false);

            CompiledGraph graph = await CompilationStartup
                .CompileAsync(
                    configuration,
                    new AgentCompilationContext(chatClients)
                    {
                        Tools = tools.Registry,
                        Guards = new GuardEvaluator(configuration.Guards, _loggers.CreateLogger<GuardEvaluator>()),
                        Moderation = PromptModerator.FromRegistry(evaluators),
                        ConversationStore = conversations,
                        Knowledge = knowledge,
                        Skills = skills,
                        Citations = KnowledgeCitationFormatterFactory.Resolve(configuration, _options.KnowledgeCitations),
                        Loggers = _loggers,
                        WorkspaceRoot = _options.WorkspaceRoot,
                        Clock = _options.TimeProvider,
                        Hooks = hooks,
                        Secrets = secrets,
                    })
                .ConfigureAwait(false);

            HookRuntime runtime = Track(graph.Entries.Values.First().Hooks);
            runtime.StopTimeout = Services?.GetService<IOptions<HostOptions>>()?.Value.ShutdownTimeout ?? HookRuntime.DefaultStopTimeout;
            auditQueue.Upstream = runtime.Notices.FlushAllAsync;

            ConversationSeamAdapters seams = ConversationSeamStartup.Build(configuration, _options);

            // A caller check whose secret is missing stops the host here, rather than refusing every call later.
            if (seams.Route is { } route)
            {
                await route.Ready.ConfigureAwait(false);
            }

            ConversationSessionSeam conversation = ConversationSessionStartup.Open(this, _options, graph, auditQueue, _loggers);

            Started = new BootState(
                configuration,
                secrets,
                telemetry,
                tools.Registry,
                conversations,
                blobs,
                evaluators,
                graph,
                conversation.Entries,
                conversation.Queue,
                knowledge,
                seams.Conversation,
                seams.Speech,
                seams.Route,
                seams.Unroutable,
                runtime);
        }

        /// <inheritdoc/>
        public async ValueTask DisposeAsync()
        {
            await CloseAsync().ConfigureAwait(false);
        }

        /// <summary>Closes everything, blocking until it is done.</summary>
        public void Dispose()
        {
            CloseAsync().AsTask().GetAwaiter().GetResult();
        }

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

            for (int index = opened.Length - 1; index >= 0; index--)
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

                        default:
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
        {
            return new(
                        "AgentCore has not booted: the document is loaded, and every adapter it names is opened, "
                        + "when the host starts. Resolve this service from a started host — await "
                        + "host.StartAsync(), or app.Run() — and not from a provider nobody started.");
        }

        private sealed record BootState(
            AgentCoreConfiguration Configuration,
            ResolvedSecrets Secrets,
            ITelemetrySession? Telemetry,
            ToolRegistry Tools,
            Conversations Conversations,
            IBlobStore? Blobs,
            EvaluatorRegistry Evaluators,
            CompiledGraph Graph,
            EntryRegistry Entries,
            QueuedAuditSink AuditQueue,
            IKnowledgeRetrievalPort? Knowledge,
            IReadOnlyList<IConversationAdapter>? ConversationAdapters,
            IReadOnlyList<ISpeechAdapter>? SpeechAdapters,
            ConversationRoute? ConversationRoute,
            string? ConversationUnroutable,
            HookRuntime Hooks);
    }

    /// <summary>Every line <see cref="AgentCoreBoot"/> writes itself, below what each startup step logs.</summary>
    internal static partial class AgentCoreBootLog
    {
        /// <summary>One structural warning from the configuration checks.</summary>
        /// <param name="logger">The boot's own logger.</param>
        /// <param name="warning">The warning's <c>ToString()</c>: its pointer, its message, and its check.</param>
        [LoggerMessage(
            EventId = 1,
            Level = LogLevel.Warning,
            Message = "{Warning}")]
        public static partial void ConfigurationWarning(ILogger logger, string warning);
    }
}
