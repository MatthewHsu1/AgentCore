using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Tools;
using Microsoft.Extensions.Logging;

namespace AgentCore.AspNetCore.DependencyInjection;

/// <summary>Everything step 5 compiled, and the two seams it needed to do it.</summary>
/// <param name="ChatClients">The factory the compile table asks for every agent and for the extractor.</param>
/// <param name="Guards">The shared evaluator. It holds no state of its own.</param>
/// <param name="Registry">The registry that compiled the document, and would compile it again.</param>
/// <param name="Compiled">The one graph every call shares.</param>
internal readonly record struct CompiledGraph(
    IChatClientFactory ChatClients,
    GuardEvaluator Guards,
    CompiledAgentRegistry Registry,
    CompiledAgent Compiled);

/// <summary>Step 5: compile the document once, so every call shares the result.</summary>
internal static class CompilationStartup
{
    /// <summary>Asks the host for its chat clients, then compiles the document against them.</summary>
    /// <param name="configuration">The loaded document.</param>
    /// <param name="options">The options the host filled. It carries the chat client seam.</param>
    /// <param name="startup">The loaded document and the resolved secrets, as the chat client seam reads them.</param>
    /// <param name="tools">The chain step 4 built.</param>
    /// <param name="loggers">The factory the guard evaluator takes its logger from.</param>
    /// <param name="cancellationToken">Cancels the chat client build.</param>
    /// <returns>The compiled graph, and the seams that made it.</returns>
    /// <exception cref="InvalidOperationException">The options bind no chat client adapter.</exception>
    /// <exception cref="ConfigurationLoadException">The document does not compile.</exception>
    /// <remarks>
    /// <para>
    /// Section 8.7, row five: a guard that throws at run time is not a defect. The evaluator already
    /// reports each distinct guard exactly once, and this is where that report finds a logger. Nothing
    /// else binds it, so an unbound evaluator would report into nothing.
    /// </para>
    /// <para>
    /// Row 4 of the compile table needs both seams, so both are bound here. The state source is
    /// <see cref="CallStateScope"/>, which finds the state of the call running on the current flow of
    /// execution, and <c>CallSession</c> opens that scope for the turn. One compiled graph therefore
    /// serves every call, exactly as T44 asks, and two calls that run at the same time take different
    /// edges.
    /// </para>
    /// </remarks>
    internal static async ValueTask<CompiledGraph> CompileAsync(
        AgentCoreConfiguration configuration,
        AgentCoreOptions options,
        AgentCoreStartup startup,
        CompositeAgentToolFactory tools,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var chatClients = await (options.ChatClients
            ?? throw new InvalidOperationException(
                "AddAgentCoreAsync binds no chat client adapter. Call options.UseChatClients(...), because the "
                + "compile table asks it for every agent and for the extractor."))
            .Invoke(startup, cancellationToken)
            .ConfigureAwait(false);

        GuardEvaluator guards = new(configuration.Guards, loggers.CreateLogger<GuardEvaluator>());
        CompiledAgentRegistry registry = new();

        var compiled = registry.GetOrCompile(
            configuration,
            new AgentCompilationContext(chatClients)
            {
                Tools = tools,
                Guards = guards,
                StateSnapshot = CallStateScope.Snapshot,
            });

        return new CompiledGraph(chatClients, guards, registry, compiled);
    }
}
