using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Evaluation;
using AgentCore.Application.Knowledge;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Tools.Registry;
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
    /// <summary>Compiles the document against the tools and the chat clients already built.</summary>
    /// <param name="configuration">The loaded document.</param>
    /// <param name="chatClients">The factory step 3c built, which the compile table asks for every agent and for the extractor.</param>
    /// <param name="tools">The registry step 4 built.</param>
    /// <param name="transcript">The store 1 backing step 4b opened. One store serves every call.</param>
    /// <param name="evaluators">
    /// The registry the moderator comes out of. R3 puts moderation in the chat pipeline of every
    /// compiled agent, so it is bound here rather than on the session factory.
    /// </param>
    /// <param name="knowledge">The port step 3b opened, or <see langword="null"/> when the host bound no knowledge vendor.</param>
    /// <param name="citations">The wording <c>providers.knowledge.citation</c> named.</param>
    /// <param name="loggers">The factory the guard evaluator and the knowledge provider take their loggers from.</param>
    /// <returns>The compiled graph, and the seams that made it.</returns>
    /// <exception cref="ConfigurationLoadException">The document does not compile.</exception>
    internal static ValueTask<CompiledGraph> CompileAsync(
        AgentCoreConfiguration configuration,
        IChatClientFactory chatClients,
        ToolRegistry tools,
        ITranscriptStore transcript,
        EvaluatorRegistry evaluators,
        IKnowledgeRetrievalPort? knowledge,
        IKnowledgeCitationFormatter citations,
        ILoggerFactory loggers)
    {
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
                Knowledge = knowledge,
                Citations = citations,
                Loggers = loggers,
            });

        return ValueTask.FromResult(new CompiledGraph(chatClients, guards, registry, compiled));
    }
}
