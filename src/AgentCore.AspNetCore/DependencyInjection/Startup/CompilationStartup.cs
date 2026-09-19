using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Ports;

namespace AgentCore.AspNetCore.DependencyInjection;

/// <summary>Everything step 5 compiled, and the two seams it needed to do it.</summary>
/// <param name="ChatClients">The factory the compile table asks for every agent and for the extractor.</param>
/// <param name="Guards">The shared evaluator. It holds no state of its own.</param>
/// <param name="Registry">The registry that compiled the document, and would compile it again.</param>
/// <param name="Entries">The compiled entries, keyed by entry name. Every conversation shares them.</param>
internal readonly record struct CompiledGraph(
    IChatClientFactory ChatClients,
    IGuardEvaluator Guards,
    CompiledAgentRegistry Registry,
    IReadOnlyDictionary<string, CompiledAgent> Entries);

/// <summary>Step 5: compile the document once, so every conversation shares the result.</summary>
internal static class CompilationStartup
{
    /// <summary>Compiles every entry of the document against the seams the earlier steps built.</summary>
    /// <param name="configuration">The loaded document.</param>
    /// <param name="context">
    /// The seams the compile table needs. <see cref="AgentCompilationContext.Guards"/> is set: R3 puts
    /// moderation and the guards in the chat pipeline of every compiled agent, so both are bound
    /// here rather than on the session factory.
    /// </param>
    /// <returns>The compiled entries, and the seams that made them.</returns>
    /// <exception cref="ConfigurationLoadException">An entry does not compile.</exception>
    internal static ValueTask<CompiledGraph> CompileAsync(AgentCoreConfiguration configuration, AgentCompilationContext context)
    {
        var guards = context.Guards
            ?? throw new InvalidOperationException("Step 5 compiles against a guard evaluator, and the context names none.");

        CompiledAgentRegistry registry = new();
        var entries = registry.EnsureAll(configuration, context);

        return ValueTask.FromResult(new CompiledGraph(context.ChatClients, guards, registry, entries));
    }
}
