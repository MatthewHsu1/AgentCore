using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Knowledge;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime.Compaction;
using AgentCore.Application.Runtime.Turn;
using AgentCore.Application.Skills;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
namespace AgentCore.Application.Configuration.Compilation;

internal static class AgentContextProviderCompiler
{
    /// <summary>
    /// The formatter an agent cites through when the host bound none. Stateless, so every agent in
    /// every document reads the one instance.
    /// </summary>
    private static readonly SourceLocatorCitationFormatter DefaultCitations = new();

    /// <summary>
    /// Builds the context providers of one agent. <paramref name="clarification"/> is the document's
    /// ambiguity wiring (§8), the same for every agent — built once by the caller rather than
    /// re-derived per agent.
    /// </summary>
#pragma warning disable MAAI001 // BackgroundAgentsProvider is evaluation-only in Microsoft.Agents.AI 1.21.0.
    public static List<AIContextProvider> Build(
        AgentDefaults? defaults,
        AgentConfiguration item,
        AgentCompilationContext context,
        string pointer,
        ResolvedClarification clarification,
        KnowledgeScopeConfiguration? scope,
        Func<string, AIAgent?> resolve,
        ICollection<BackgroundAgentsProvider>? background = null)
    {
        List<AIContextProvider> providers = [new TurnContextProvider()];

        if (item.Skills.Count > 0)
        {
            if (context.Skills is not { } catalog)
            {
                throw ConfigurationCompiler.Fail(
                    ConfigurationError.AppendPointer(pointer, "skills"),
                    $"the agent '{item.Id}' declares a skills: list and this host bound no skills "
                    + "folder, so there is nothing to load. Call options.UseSkills(...) with the "
                    + "folder that holds the SKILL.md directories, or remove the skills: list.");
            }

            providers.Add(SkillsProviderFactory.Create(catalog, item.Skills, context.Loggers));
        }

        ResolvedCompaction? compaction;
        try
        {
            compaction = AgentCompaction.Compose(defaults, item);
        }
        catch (ArgumentException exception)
        {
            throw ConfigurationCompiler.Fail(
                ConfigurationError.AppendPointer(ConfigurationError.AppendPointer(pointer, "compaction"), "trigger"),
                exception.Message);
        }

        // Bound ahead of the knowledge early-return below, for the same reason: a compaction: block
        // is independent of whether this agent composes a knowledge: block.
        if (compaction is { } resolved)
        {
#pragma warning disable MAAI001 // Compaction is evaluation-only in Microsoft.Agents.AI 1.17.0.
            providers.Add(new CompactionProvider(
                CompactionStrategyFactory.Create(resolved),
                loggerFactory: context.Loggers));
#pragma warning restore MAAI001
        }
        AgentHarnessProviders.Add(providers, defaults, item, context, pointer, resolve, background);

        if (AgentKnowledge.Compose(defaults, item) is not { } composed)
        {
            return providers;
        }

        if (context.Knowledge is not { } port)
        {
            throw ConfigurationCompiler.Fail(
                ConfigurationError.AppendPointer(pointer, "knowledge"),
                $"the agent '{item.Id}' declares a knowledge: block and this host registered no "
                + "knowledge vendor, so there is no store to read. Call "
                + "options.UseKnowledgeStores(...) with an adapter that serves "
                + $"{nameof(IKnowledgeRetrievalPort)}, or remove the knowledge: block.");
        }

        // The document-level wiring, carried onto this agent's own resolved knowledge so the search
        // side does not have to re-derive it.
        var knowledge = composed with { Clarification = clarification };

        providers.Add(KnowledgeProviderFactory.Create(
            port,
            knowledge,
            item.Id,
            context.Citations ?? DefaultCitations,
            context.Loggers,
            scope));

        return providers;
    }
#pragma warning restore MAAI001
}
