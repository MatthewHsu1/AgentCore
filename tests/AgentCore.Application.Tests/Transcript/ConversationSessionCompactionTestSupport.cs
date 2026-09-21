using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Conversation;
using AgentCore.Application.Conversation.Memory;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Runtime.Compaction;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Transcript;
using AgentCore.TestSupport;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentCore.Application.Tests.Transcript
{
    /// <summary>Builds the session and seeds the store the compaction facts run against.</summary>
#pragma warning disable MAAI001 // Compaction is evaluation-only in Microsoft.Agents.AI 1.21.0.
    internal static class ConversationSessionCompactionTestSupport
    {
        /// <param name="compaction">
        /// The stages the agent runs in place of the window-derived ones: sized to fire on a handful of
        /// fixture messages.
        /// </param>
        /// <param name="contextWindow">The reply model's window, in tokens. The fixed stages fire at 75% of it (D7).</param>
        internal static ConversationSession CreateSession(
            IChatClient reply, IConversationStore store, string? conversationId = null, ILogger? logger = null, CompactionStages? compaction = null, int contextWindow = 128_000, IConversationObserver? observer = null)
        {
            AgentCoreConfiguration document = ConfigurationLoader.LoadYaml(ConversationSessionResumeTestSupport.OneAgentYaml);
            FakeChatClientFactory chatClients = new(reply, contextWindow);
            CompiledAgent compiled = ConfigurationCompiler.CompileAll(
                document,
                new AgentCompilationContext(chatClients)
                {
                    ConversationStore = store,
                    Tools = TestToolRegistry.From(document, null, TestContext.Current.CancellationToken),
                    Compaction = compaction,
                })["main"];

            ConversationSessionFactory factory = new(
                compiled,
                new GuardEvaluator(compiled.Configuration.Guards),
                extractor: null,
                logger: logger,
                observers: observer is null ? [] : [observer]);

            return factory.Create(conversationId);
        }

        /// <summary>Stages whose cap never fires and whose summary always does.</summary>
        internal static CompactionStages Summary(IChatClient summariser, int minimumPreservedGroups)
        {
            return SummaryOnly(summariser, client => new SummarizationCompactionStrategy(client, CompactionTriggers.Always, minimumPreservedGroups));
        }

        /// <summary>Stages whose cap never fires, over a bespoke strategy that answers no client of its own.</summary>
        internal static CompactionStages SummaryOnly(CompactionStrategy summary)
        {
            return SummaryOnly(new ScriptedChatClient("unused"), _ => summary);
        }

        /// <summary>Stages whose cap never fires, built from the summariser and the strategy factory the provider itself wraps.</summary>
        internal static CompactionStages SummaryOnly(IChatClient summariser, Func<IChatClient, CompactionStrategy> summary)
        {
            return new(new ToolResultCapProvider(CompactionTriggers.Never, keepTurns: 0, maxResultChars: 1), summariser, summary);
        }

        /// <summary>A store holding conversation <c>c1</c> with three plain turns, enough for a summary pass to have something to fold.</summary>
        internal static async Task<InMemoryConversationStore> SeededStoreAsync()
        {
            InMemoryConversationStore store = new();
            _ = await store.CreateAsync("c1", TestContext.Current.CancellationToken);
            await SeedPlainTurnAsync(store, 0, "q0", "a0");
            await SeedPlainTurnAsync(store, 1, "q1", "a1");
            await SeedPlainTurnAsync(store, 2, "q2", "a2");
            return store;
        }

        /// <summary>Seeds one turn of a plain question and reply, two rows.</summary>
        internal static async Task SeedPlainTurnAsync(IConversationStore store, int turnIndex, string question, string reply)
        {
            _ = await store.AppendAsync(
                "c1",
                [
                    new ConversationMessageDraft(turnIndex, new ChatMessage(ChatRole.User, question), $"m-{turnIndex}-q"),
                    new ConversationMessageDraft(turnIndex, new ChatMessage(ChatRole.Assistant, reply), $"m-{turnIndex}-a"),
                ],
                new ConversationSessionState { NextTurnIndex = turnIndex + 1 },
                TestContext.Current.CancellationToken);
        }

        /// <summary>Seeds one turn of a question, a tool call and its result, and a reply — four rows.</summary>
        internal static async Task SeedToolTurnAsync(
            InMemoryConversationStore store, int turnIndex, string question, string toolName, string callId, string result, string reply)
        {
            _ = await store.AppendAsync(
                "c1",
                [
                    new ConversationMessageDraft(turnIndex, new ChatMessage(ChatRole.User, question), $"m-{turnIndex}-q"),
                    new ConversationMessageDraft(
                        turnIndex,
                        new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(callId, toolName, new Dictionary<string, object?>(StringComparer.Ordinal))]),
                        $"m-{turnIndex}-call"),
                    new ConversationMessageDraft(turnIndex, new ChatMessage(ChatRole.Tool, [new FunctionResultContent(callId, result)]), $"m-{turnIndex}-result"),
                    new ConversationMessageDraft(turnIndex, new ChatMessage(ChatRole.Assistant, reply), $"m-{turnIndex}-a"),
                ],
                new ConversationSessionState { NextTurnIndex = turnIndex + 1 },
                TestContext.Current.CancellationToken);
        }
    }
#pragma warning restore MAAI001
}
