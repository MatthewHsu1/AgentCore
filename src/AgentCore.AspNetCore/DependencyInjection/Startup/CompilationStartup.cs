using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Evaluation;
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
    /// <param name="tools">The registry step 4 built.</param>
    /// <param name="transcript">The store 1 backing step 4b opened. One store serves every call.</param>
    /// <param name="evaluators">
    /// The registry the moderator comes out of. R3 puts moderation in the chat pipeline of every
    /// compiled agent, so it is bound here rather than on the session factory.
    /// </param>
    /// <param name="loggers">The factory the guard evaluator takes its logger from.</param>
    /// <param name="cancellationToken">Cancels the chat client build.</param>
    /// <returns>The compiled graph, and the seams that made it.</returns>
    /// <exception cref="InvalidOperationException">The options bind no chat client adapter.</exception>
    /// <exception cref="ConfigurationLoadException">The document does not compile.</exception>
    internal static async ValueTask<CompiledGraph> CompileAsync(
        AgentCoreConfiguration configuration,
        AgentCoreOptions options,
        AgentCoreStartup startup,
        ToolRegistry tools,
        ITranscriptStore transcript,
        EvaluatorRegistry evaluators,
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
                Moderation = PromptModerator.FromRegistry(evaluators),
                TranscriptStore = transcript,
                StateSnapshot = CallStateScope.Snapshot,
            });

        return new CompiledGraph(chatClients, guards, registry, compiled);
    }
}
