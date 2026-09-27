using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Conversation.Memory;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Sessions.Memory;
using AgentCore.Application.Transcript;
using AgentCore.AspNetCore.Sessions;
using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.TestSupport;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Sessions
{
    /// <summary>
    /// MAF's own <see cref="AgentSessionStore"/> path — <c>GetSessionAsync</c>/<c>SaveSessionAsync</c> called
    /// directly, the way an AG-UI or A2A host would, without ever going through the Responses endpoint.
    /// </summary>
    public sealed class AgentCoreAgentSessionStoreTests
    {
        private const string Yaml =
            """
            apiVersion: agentcore/v1
            agents:
              defaults:
                model: { ref: reply }
              items:
                - { id: greeter, instructions: "greet the caller" }
            providers:
              conversation:   { kind: telnyx-relay }
              speech:
                stt: { kind: telnyx-relay }
                tts: { kind: telnyx-relay }
              llm:
                - { kind: openai, model: gpt-4.1-mini, as: reply }
            entries:
              main:
                agent: greeter
            """;

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        [Fact]
        public async Task AnUnknownKey_KeepsResolvingToTheSameConversation_PastTheRetentionWindow()
        {
            FakeTimeProvider clock = new(DateTimeOffset.UtcNow);
            InMemoryConversationStore store = new(clock);
            AgentCoreAgentSessionStore sessions = new(store);
            AgentCoreAgent agent = BuildAgent(new FragmentingChatClient("hello there"), store);

            // MAF's own path: a key nothing here minted, opened and saved back with no help from
            // ResponsesEndpoints.
            AgentSession first = await sessions.GetSessionAsync(agent, "external-thread-1", Ct);
            _ = await agent.RunAsync("hi", first, cancellationToken: Ct);
            await sessions.SaveSessionAsync(agent, "external-thread-1", first, Ct);

            // D3: the key already names its own conversation row, so SaveSessionAsync wrote no response
            // row for it — nothing here for a sweep to find.
            clock.Advance(TimeSpan.FromDays(31));
            int swept = await store.SweepAsync(TimeSpan.FromDays(30), cancellationToken: Ct);
            Assert.Equal(0, swept);

            AgentSession resumed = await sessions.GetSessionAsync(agent, "external-thread-1", Ct);
            ConversationSession conversation = resumed.GetService<ConversationSession>()!;
            IReadOnlyList<ConversationMessage> rows = await store.ReadForSessionAsync(conversation.ConversationId, Ct);

            Assert.Equal("external-thread-1", conversation.ConversationId);
            Assert.Contains(rows, row => row.Content.Role == ChatRole.User && row.Content.Text == "hi");
        }

        private static AgentCoreAgent BuildAgent(IChatClient reply, IConversationStore store)
        {
            CompiledAgent compiled = ConfigurationCompiler.CompileAll(
                ConfigurationLoader.LoadYaml(Yaml),
                new AgentCompilationContext(new RoutingChatClientFactory(reply)) { ConversationStore = store })["main"];

            InMemoryConversationSessions sessions = new(
                new Dictionary<string, IConversationSessionFactory>(StringComparer.Ordinal)
                {
                    ["main"] = new ConversationSessionFactory(compiled, new GuardEvaluator(compiled.Configuration.Guards)),
                },
                TimeSpan.FromMinutes(30),
                TimeProvider.System);

            return new AgentCoreAgent(sessions, "main");
        }
    }
}
