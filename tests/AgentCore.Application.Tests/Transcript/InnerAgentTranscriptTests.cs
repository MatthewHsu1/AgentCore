using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Tests.Fakes;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.Application.Tests.Transcript
{
    /// <summary>
    /// Pins the isolation a <c>kind: agent</c> tool sells: the outer agent buys the inner agent's
    /// answer, never its working-out.
    /// </summary>
    public sealed class InnerAgentTranscriptTests
    {
        private const string DelegatingYaml = """
          apiVersion: agentcore/v1
          tools:
            - { id: call_helper, kind: agent, agent: helper, description: "ask the helper" }
          agents:
            items:
              - { id: main, instructions: "answer the caller", tools: [ call_helper ] }
              - { id: helper, instructions: "look things up" }
          entries:
            main:
              policy:
                initial: talk
                stages:
                  - id: talk
                    agent: main
                    terminal: true
          """;

        /// <summary>
        /// The inner agent runs on a session of its own, which no <c>BeginConversation</c> ever named, so the
        /// conversation's transcript never sees its rounds. Keeping them would cost tokens on every later turn and would put the
        /// inner agent's working-out into the audit record, which no consumer asked for.
        /// </summary>
        [Fact]
        public async Task ADelegatingTurn_WritesTheOuterRoundsOnly()
        {
            RecordingConversationStore store = new();
            using ToolCallingChatClient model = new("done");
            CompiledAgent compiled = ConfigurationCompiler.CompileAll(
                ConfigurationLoader.LoadYaml(DelegatingYaml),
                new AgentCompilationContext(new FakeChatClientFactory(model)) { ConversationStore = store })["main"];

            ConversationSession session = new ConversationSessionFactory(
                compiled, new GuardEvaluator(compiled.Configuration.Guards), extractor: null).Create();

            await foreach (ChatResponseUpdate _ in session.RunTurnStreamingAsync("hi", TestContext.Current.CancellationToken))
            {
            }

            await session.FlushTranscriptAsync();

            // Outer tool call, inner answer, outer reply: the inner agent's model is reached, and its
            // working-out still stays off the outer conversation's rows.
            Assert.Equal(3, model.Calls);
            Assert.Equal(
                ["user", "assistant", "tool", "assistant"],
                store.Rows.Select(row => row.Content.Role.Value));
            Assert.All(store.Rows, row => Assert.Equal(session.ConversationId, row.ConversationId));
        }
    }
}
