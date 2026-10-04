using AgentCore.Application.Audit.Memory;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Hooks;
using AgentCore.Application.Ports;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Domain;
using AgentCore.Domain.Audit;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.Application.Tests.Transcript
{
    /// <summary>
    /// What a store that refuses a write costs the conversation: nothing, and never its end.
    /// </summary>
    public sealed class ConversationSessionStoreFailureTests
    {
        private const string OneAgentYaml = """
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

        private const string EndsOnTheSecondTurnYaml = """
        apiVersion: agentcore/v1
        guards:
          secondTurn: { ">=": [ { var: turnIndex }, 2 ] }
        agents:
          items:
            - { id: only, instructions: "ok" }
        entries:
          main:
            policy:
              initial: working
              stages:
                - { id: working, agent: only, to: [ { stage: done, when: secondTurn } ] }
                - { id: done, agent: only, terminal: true }
        """;

        /// <summary>A message store write failure never ends a conversation.</summary>
        [Fact]
        public async Task Append_StoreThrows_ConversationContinues()
        {
            using RequestRecordingChatClient reply = new("hi there", "it ships Friday");
            ConversationSession session = CreateSession(OneAgentYaml, reply, new ThrowingConversationStore());
            _ = await session.RunTurnAsync("hello", TestContext.Current.CancellationToken);

            TurnResult second = await session.RunTurnAsync("order 41?", TestContext.Current.CancellationToken);

            // The live history is the session's, so the turn after a dropped write still has the
            // whole conversation. Only the durable copy was lost.
            Assert.Equal("it ships Friday", second.ReplyText);
            Assert.Equal(
                ["user:hello", "assistant:hi there", "user:order 41?"],
                reply.Requests[1]);
        }

        /// <summary>
        /// A dropped write is a fact about the system and never about the conversation: the audit chain holds what it
        /// would hold with no failure at all, and the dropped write itself takes no row.
        /// </summary>
        [Fact]
        public async Task Append_StoreThrows_TakesNoAuditRow()
        {
            using RequestRecordingChatClient reply = new("hi there");
            InMemoryAuditSink sink = new();
            ConversationSession session = CreateSession(
                OneAgentYaml, reply, new ThrowingConversationStore(), BuiltInHooks.Create(sink, NullLogger.Instance));

            _ = await session.RunTurnAsync("hello", TestContext.Current.CancellationToken);
            await session.FlushTranscriptAsync();
            await session.FlushNoticesAsync();

            IReadOnlyList<AuditEvent> rows = sink.EventsOf(session.ConversationId);
            Assert.Equal([AuditEventKind.ConversationStarted, AuditEventKind.TurnCompleted], rows.Select(row => row.Kind));
            Assert.Equal(rows.Count, rows.Select(row => row.EventId).Distinct().Count());
        }

        /// <summary>
        /// A turn that ended the conversation ended it, even when the store dropped its words: the next turn is
        /// refused, not run on the earlier stage the store still holds, because no row may follow <c>conversation.ended</c>.
        /// </summary>
        [Fact]
        public async Task Append_StoreThrowsOnTheTurnThatEndsTheConversation_TheNextTurnIsRefused()
        {
            using RequestRecordingChatClient reply = new("hi there", "goodbye", "still here?");
            FlakyAppendConversationStore store = new();
            ConversationSession session = CreateSession(EndsOnTheSecondTurnYaml, reply, store);
            Assert.False((await session.RunTurnAsync("hello", TestContext.Current.CancellationToken)).IsTerminal);
            store.Down = true;
            Assert.True((await session.RunTurnAsync("bye", TestContext.Current.CancellationToken)).IsTerminal);
            await session.FlushTranscriptAsync();
            store.Down = false;

            InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(
                () => session.RunTurnAsync("one more", TestContext.Current.CancellationToken));

            Assert.Contains("runs no further turn", refused.Message, StringComparison.Ordinal);
            Assert.Equal(2, reply.Requests.Count);
        }

        private static ConversationSession CreateSession(
            string yaml, IChatClient reply, IConversationStore store, IReadOnlyList<AgentHook>? hooks = null)
        {
            AgentCoreConfiguration document = ConfigurationLoader.LoadYaml(yaml);
            CompiledAgent compiled = ConfigurationCompiler.CompileAll(
                document,
                new AgentCompilationContext(new FakeChatClientFactory(reply)) { ConversationStore = store })["main"];

            return new ConversationSessionFactory(
                compiled,
                new GuardEvaluator(compiled.Configuration.Guards),
                extractor: null,
                hooks: hooks).Create();
        }
    }
}
