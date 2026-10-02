using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Knowledge;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime.Compaction;
using AgentCore.Application.Runtime.Turn;
using AgentCore.Application.Skills;
using AgentCore.Application.Transcript;
using Microsoft.Agents.AI;

namespace AgentCore.Application.Configuration.Compilation
{
    internal static class AgentContextProviderCompiler
    {
        /// <summary>
        /// The formatter an agent cites through when the host bound none. Stateless, so every agent in
        /// every document reads the one instance.
        /// </summary>
        private static readonly SourceLocatorCitationFormatter DefaultCitations = new();

        /// <summary>Builds the context providers of one agent.</summary>
        /// <param name="walk">What every agent of this walk shares.</param>
        /// <param name="item">The agent being compiled.</param>
        /// <param name="context">The compile-time seams.</param>
        /// <param name="pointer">This agent's JSON pointer.</param>
        /// <param name="resolve">Resolves an <c>agents.items</c> id to its compiled agent, or <see langword="null"/> when undeclared.</param>
        /// <param name="history">Store 1, or <see langword="null"/> on a row whose session carries no history and so has nothing to summarise.</param>
        public static List<AIContextProvider> Build(
            AgentsWalk walk,
            AgentConfiguration item,
            AgentCompilationContext context,
            string pointer,
            Func<string, AIAgent?> resolve,
            AgentCoreChatHistoryProvider? history)
        {
            AgentDefaults? defaults = walk.Defaults;
            List<AIContextProvider> providers = [];

            if (item.Pinned.Count > 0)
            {
                providers.Add(new PinnedSkillsProvider(RequireCatalog(item, context, pointer, "pinned"), item.Pinned));
            }

            providers.Add(new TurnContextProvider());

            if (item.Skills.Count > 0)
            {
                providers.Add(SkillsProviderFactory.Create(RequireCatalog(item, context, pointer, "skills"), item.Skills, item.Pinned, context.Loggers));
            }

            ModelReference? model = item.Model ?? defaults?.Model;
            if (context.ChatClients.GetContextWindow(model) is not { } contextWindow)
            {
                throw ConfigurationCompiler.Fail(
                    ConfigurationError.AppendPointer(pointer, "model"),
                    $"the agent '{item.Id}' uses the model '{model?.Ref ?? "default"}' and its adapter "
                    + "reports no context window, so compaction cannot size itself. Add the model to the "
                    + "adapter's table.");
            }

            providers.Add(new ReaderContentFilterProvider());

            CompactionStages stages = context.Compaction ?? CompactionStrategyFactory.Create(context.ChatClients.GetChatClient(model), contextWindow);
            providers.AddRange(CompactionStrategyFactory.BuildProviders(stages, history));

            AgentHarnessProviders.Add(providers, defaults, item, context, pointer, resolve, walk.Background);

            if (AgentKnowledge.Compose(defaults, item) is not { } composed)
            {
                return providers;
            }

            if (context.Knowledge is not { } port)
            {
                throw ConfigurationCompiler.Fail(
                    ConfigurationError.AppendPointer(pointer, "knowledge"),
                    $"the agent '{item.Id}' declares a knowledge: block and this host registered no "
                    + "knowledge vendor, so there is no store to read. Conversation "
                    + "options.UseKnowledgeStores(...) with an adapter that serves "
                    + $"{nameof(IKnowledgeRetrievalPort)}, or remove the knowledge: block.");
            }

            ResolvedKnowledge knowledge = composed with { Clarification = walk.Clarification };

            providers.Add(KnowledgeProviderFactory.Create(
                port,
                knowledge,
                item.Id,
                context.Citations ?? DefaultCitations,
                context.Loggers,
                walk.Scope));

            return providers;
        }

        private static SkillCatalog RequireCatalog(AgentConfiguration item, AgentCompilationContext context, string pointer, string key)
        {
            return context.Skills ?? throw ConfigurationCompiler.Fail(
                ConfigurationError.AppendPointer(pointer, key),
                $"the agent '{item.Id}' declares a {key}: list and this host bound no skills "
                + "folder, so there is nothing to load. Call options.UseSkills(...) with the "
                + $"folder that holds the SKILL.md directories, or remove the {key}: list.");
        }
    }
}
