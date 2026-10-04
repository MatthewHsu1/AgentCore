using AgentCore.TestSupport;
using AgentCore.Application.Audit.Memory;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Hooks;
using AgentCore.Application.Ports;
using AgentCore.Domain.Audit;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Xunit;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.Application.Tests.Audit
{
    /// <summary>What every part of the audit test suite shares: the two fixture documents, the two
    /// extractor replies, and the plumbing to compile one and open a session against it.</summary>
    internal static class ConversationSessionAuditTestSupport
    {
        internal const string PolicyYaml =
            """
          apiVersion: agentcore/v1
          state:
            callerSaidGoodbye: { type: boolean, default: false, writer: extractor }
          guards:
            saidGoodbye: { var: callerSaidGoodbye }
          extractor:
            model: { ref: fill }
            when: after_reply
          agents:
            defaults:
              model: { ref: reply }
            items:
              - { id: greeter, instructions: "greet the caller" }
              - { id: closer,  instructions: "close the conversation" }
          entries:
            main:
              policy:
                initial: greeting
                stages:
                  - { id: greeting, agent: greeter, to: [ { stage: close, when: saidGoodbye } ] }
                  - { id: close,    agent: closer,  terminal: true }
          """;

        internal const string ToolYaml =
            """
        apiVersion: agentcore/v1
        tools:
          - { id: lookup_order, kind: builtin, uses: orders.read, description: "Look up an order by its id." }
        agents:
          defaults:
            model: { ref: reply }
          items:
            - { id: only, instructions: "I answer everything", tools: [ lookup_order ] }
        entries:
          main:
            agent: only
        """;

        internal const string StayingNull = /*lang=json,strict*/ """{ "callerSaidGoodbye": null }""";
        internal const string SaidGoodbye = /*lang=json,strict*/ """{ "callerSaidGoodbye": true }""";

        internal static ConversationSessionFactory Build(
            string yaml,
            IChatClient reply,
            IChatClient? fill,
            Func<ToolConfiguration, AITool?>? tools = null,
            TimeProvider? timeProvider = null,
            IAuditSinkPort? auditSink = null,
            ILogger? logger = null,
            IConversationStore? store = null)
        {
            // The audit hook takes a required sink, because the composition root resolves providers.audit for
            // every host and falls back to the in-process memory kind. An optional parameter has to be a
            // compile-time constant, so the default is spelled here instead — a fact that does not care where
            // its events land gets a fresh in-memory sink.
            IAuditSinkPort sink = auditSink ?? new InMemoryAuditSink();

            AgentCoreConfiguration document = ConfigurationLoader.LoadYaml(yaml);
            RoutingChatClientFactory chatClients = new(reply);
            if (fill is not null)
            {
                _ = chatClients.Route("fill", fill);
            }

            CompiledAgent compiled = ConfigurationCompiler.CompileAll(
                document,
                new AgentCompilationContext(chatClients)
                {
                    Tools = TestToolRegistry.From(document, tools, TestContext.Current.CancellationToken),
                    ConversationStore = store,
                })["main"];

            return new ConversationSessionFactory(
                compiled,
                new GuardEvaluator(compiled.Configuration.Guards),
                ConversationSessionFactory.CreateExtractor(compiled, chatClients),
                timeProvider,
                logger,
                hooks: BuiltInHooks.Create(sink, logger));
        }

        /// <summary>Waits for the audit hook, then reads the conversation's rows.</summary>
        internal static async Task<IReadOnlyList<AuditEvent>> RowsAsync(this ConversationSession session, InMemoryAuditSink sink)
        {
            await session.FlushNoticesAsync();
            return sink.EventsOf(session.ConversationId);
        }
    }
}
