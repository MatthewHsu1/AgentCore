using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Hooks;
using AgentCore.Application.Ports;
using AgentCore.Application.Tests.Fakes;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.Application.Tests.Transcript
{
    /// <summary>The documents and session-building helpers a conversation's resume tests share.</summary>
    internal static class ConversationSessionResumeTestSupport
    {
        internal const string OneAgentYaml = """
        apiVersion: agentcore/v1
        agents:
          # These tests read the exact messages the model sees; the clock line would be one more.
          defaults: { clock: false }
          items:
            - { id: only, instructions: "ok" }
        entries:
          main:
            agent: only
        """;

        /// <summary>
        /// A document with something to forget: one slot a writer fills, and a stage that moves.
        /// </summary>
        internal const string StagedYaml = """
          apiVersion: agentcore/v1
          state:
            turnsTaken:
              type: integer
              default: 0
              writer: counter
              increment: { ">=": [ { var: turnIndex }, 0 ] }
          guards:
            pastFirstTurn: { ">": [ { var: turnIndex }, 0 ] }
          agents:
            items:
              - { id: intake, instructions: "ask the caller for the model" }
              - { id: help,   instructions: "help the caller" }
          entries:
            main:
              policy:
                initial: intake
                stages:
                  - { id: intake, agent: intake, to: [ { stage: help, when: pastFirstTurn } ] }
                  - { id: help,   agent: help }
          """;

        /// <summary>
        /// A document whose stages run three deep, so a machine left in the first one is visible.
        /// </summary>
        internal const string ThreeStageYaml = """
          apiVersion: agentcore/v1
          guards:
            always: { ">=": [ { var: turnIndex }, 0 ] }
          agents:
            items:
              - { id: first,  instructions: "greet the caller" }
              - { id: second, instructions: "ask the caller for the model" }
              - { id: third,  instructions: "help the caller" }
          entries:
            main:
              policy:
                initial: one
                stages:
                  - { id: one,   agent: first,  to: [ { stage: two,   when: always } ] }
                  - { id: two,   agent: second, to: [ { stage: three, when: always } ] }
                  - { id: three, agent: third }
          """;

        internal static async Task<string> FirstTurnAsync(
            IConversationStore store, string said, string heard)
        {
            using ScriptedChatClient reply = new(heard);
            ConversationSession session = CreateSession(OneAgentYaml, reply, store);

            _ = await session.RunTurnAsync(said, TestContext.Current.CancellationToken);
            await session.FlushTranscriptAsync();

            return session.ConversationId;
        }

        internal static async Task SecondTurnAsync(
            IConversationStore store, string conversationId, string said, string heard)
        {
            using ScriptedChatClient reply = new(heard);
            ConversationSession session = CreateSession(OneAgentYaml, reply, store, conversationId);

            _ = await session.RunTurnAsync(said, TestContext.Current.CancellationToken);
            await session.FlushTranscriptAsync();
        }

        internal static ConversationSession CreateSession(
            string yaml,
            IChatClient reply,
            IConversationStore store,
            string? conversationId = null,
            AgentHook? hook = null)
        {
            AgentCoreConfiguration document = ConfigurationLoader.LoadYaml(yaml);
            FakeChatClientFactory chatClients = new(reply);
            CompiledAgent compiled = ConfigurationCompiler.CompileAll(
                document,
                new AgentCompilationContext(chatClients)
                {
                    ConversationStore = store,
                    Tools = TestToolRegistry.From(document, null, TestContext.Current.CancellationToken),
                })["main"];

            ConversationSessionFactory factory = new(
                compiled,
                new GuardEvaluator(compiled.Configuration.Guards),
                extractor: null,
                hooks: hook is null ? null : [hook]);

            return factory.Create(conversationId);
        }
    }
}
