using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Runtime;
using AgentCore.Application.Secrets;
using AgentCore.Application.Tools;
using AgentCore.AspNetCore.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.AspNetCore.DependencyInjection;

/// <summary>
/// The composition root. It turns one document into the services a host resolves.
/// </summary>
/// <remarks>
/// <para>
/// Everything happens while the host starts, in one order: load the document, run checks 2 to 8,
/// resolve every <c>${secret:name}</c> once, build the tool factory chain, compile through
/// <see cref="CompiledAgentRegistry"/>, and register the seam a call arrives on. A configuration
/// defect therefore stops the process with a message that names the fault and points into the
/// document. Nothing is deferred to the first call.
/// </para>
/// <para>
/// <see cref="CompiledAgent"/> is a process singleton by design, and it is registered as one.
/// <see cref="CallSession"/> is not, and it is registered nowhere: one call gets one session from
/// <see cref="ICallSessionFactory"/>, and <see cref="ICallSessionStore"/> holds it between requests.
/// </para>
/// </remarks>
public static class AgentCoreServiceCollectionExtensions
{
    /// <summary>Loads one document and registers everything a call needs to run on it.</summary>
    /// <param name="services">The service collection of the host.</param>
    /// <param name="configure">Binds the document and the adapters the document names.</param>
    /// <returns>The same collection, so a host chains its calls.</returns>
    /// <exception cref="InvalidOperationException">
    /// The options name no document, name two, or bind no chat client adapter.
    /// </exception>
    /// <exception cref="ConfigurationLoadException">The document fails one of the eight checks, or does not compile.</exception>
    /// <exception cref="SecretResolutionException">One <c>${secret:name}</c> reference resolves to nothing.</exception>
    public static IServiceCollection AddAgentCore(this IServiceCollection services, Action<AgentCoreOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        AgentCoreOptions options = new();
        configure(options);

        // Step 1: load.
        var configuration = LoadDocument(options);

        // Step 2: validate. Checks 2 to 8 report every defect at once, so one start names them all.
        ConfigurationValidator.Validate(configuration);

        // Step 3: resolve every secret once. A tool call then costs no lookup, and a missing
        // credential stops the host rather than one turn.
        var secrets = ResolvedSecrets
            .ResolveAsync(configuration, options.SecretResolver ?? NoSecretResolver.Instance)
            .AsTask()
            .GetAwaiter()
            .GetResult();

        AgentCoreStartup startup = new(configuration, secrets);

        // Step 4: build the tool factory chain. A link answers null for a kind it does not serve, and
        // the composite fails the start when no link serves a kind the document declares.
        var tools = BuildToolFactory(options, startup);

        // Step 5: compile. The registry compiles once and every call shares the result.
        var chatClients = (options.ChatClients
            ?? throw new InvalidOperationException(
                "AddAgentCore binds no chat client adapter. Call options.UseChatClients(...), because the "
                + "compile table asks it for every agent and for the extractor."))
            .Invoke(startup);

        GuardEvaluator guards = new(configuration.Guards);
        CompiledAgentRegistry registry = new();

        // Guards and StateSnapshot stay unbound. Both belong to a guarded graph edge, and a guarded
        // edge reads the state of one call, which this composition holds inside the session and not
        // here. The compile table refuses such an edge rather than making it unconditional.
        var compiled = registry.GetOrCompile(configuration, new AgentCompilationContext(chatClients) { Tools = tools });

        // Step 6: register. Everything above is shared and read-only for the life of the process.
        CallSessionFactory sessions = new(
            compiled,
            guards,
            CallSessionFactory.CreateExtractor(compiled, chatClients),
            options.TimeProvider);

        services.AddSingleton(configuration);
        services.AddSingleton(secrets);
        services.AddSingleton(options.Bindings);
        services.AddSingleton(registry);
        services.AddSingleton(compiled);
        services.AddSingleton<IChatClientFactory>(_ => chatClients);
        services.AddSingleton<IAgentToolFactory>(tools);
        services.AddSingleton<IGuardEvaluator>(guards);
        services.AddSingleton<ICallSessionFactory>(sessions);

        // The default store holds every call in this process. A host that registered another one
        // before this call keeps it.
        services.TryAddSingleton<ICallSessionStore, InMemoryCallSessionStore>();

        return services;
    }

    /// <summary>Reads the one document the options name.</summary>
    /// <param name="options">The options the host filled.</param>
    /// <returns>The loaded document.</returns>
    /// <exception cref="InvalidOperationException">The options name no document, or name two.</exception>
    private static AgentCoreConfiguration LoadDocument(AgentCoreOptions options)
    {
        var hasPath = options.ConfigurationPath is { Length: > 0 };
        if (options.Configuration is { } loaded)
        {
            if (hasPath)
            {
                throw new InvalidOperationException(
                    "AddAgentCore names two documents: options.Configuration holds one and "
                    + "options.ConfigurationPath names another. Set one of the two.");
            }

            return loaded;
        }

        if (!hasPath)
        {
            throw new InvalidOperationException(
                "AddAgentCore names no document. Set options.ConfigurationPath to a .yaml, .yml, or .json "
                + "file, or set options.Configuration to a document the host already loaded.");
        }

        return ConfigurationLoader.LoadFile(options.ConfigurationPath!);
    }

    /// <summary>Builds the one tool factory the compile table asks.</summary>
    /// <param name="options">The options the host filled.</param>
    /// <param name="startup">The loaded document and the resolved secrets.</param>
    /// <returns>The composite, over every link the host bound.</returns>
    private static CompositeAgentToolFactory BuildToolFactory(AgentCoreOptions options, AgentCoreStartup startup)
    {
        List<IAgentToolFactory> links = [];

        if (options.Knowledge is { } knowledge)
        {
            links.Add(new BuiltinToolFactory(knowledge(startup)));
        }

        // The binding link needs no adapter. The registry is the seam the host already filled.
        links.Add(new BindingToolFactory(options.Bindings));

        foreach (var extra in options.ToolFactories)
        {
            links.Add(extra(startup));
        }

        return new CompositeAgentToolFactory(links);
    }

    /// <summary>The chain a host that bound none gets: it holds no name at all.</summary>
    /// <remarks>
    /// A document that references no secret resolves cleanly against this. A document that references
    /// one fails, and the failure names the reference and its JSON Pointer.
    /// </remarks>
    private sealed class NoSecretResolver : ISecretResolverPort
    {
        public static NoSecretResolver Instance { get; } = new();

        public ValueTask<string?> TryResolveAsync(string name, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<string?>(null);
    }
}
