using AgentCore.Application.Audit.Memory;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Tests.Fakes;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Runtime
{
    /// <summary>
    /// The YAML documents and the session factory every interruption fact builds a
    /// <see cref="ConversationSession"/> from.
    /// </summary>
    internal static class InterruptionSessions
    {
        internal const string NoToolYaml =
            """
        apiVersion: agentcore/v1
        agents:
          items:
            - { id: only, instructions: "I answer everything" }
        entries:
          main:
            agent: only
        """;

        internal const string ToolYaml =
            """
        apiVersion: agentcore/v1
        tools:
          - { id: price_lookup, kind: builtin, uses: orders.read, description: "Look up the price of an item." }
        agents:
          items:
            - { id: only, instructions: "quote the price", tools: [ price_lookup ] }
        entries:
          main:
            agent: only
        """;

        internal const string ParallelToolYaml =
            """
        apiVersion: agentcore/v1
        tools:
          - { id: quote, kind: builtin, uses: orders.read, description: "Get a price quote for an item." }
        agents:
          items:
            - { id: only, instructions: "quote both items", tools: [ quote ] }
        entries:
          main:
            agent: only
        """;

        internal const string ExtractorYaml =
            """
        apiVersion: agentcore/v1
        state:
          callerName:
            type: string
            writer: extractor
            description: the name the caller gave
        extractor:
          model: { ref: fill }
          when: after_reply
        agents:
          items:
            - { id: only, instructions: "greet the caller" }
        entries:
          main:
            agent: only
        """;

        internal static ConversationSession CreateSession(
            string yaml,
            IChatClient reply,
            Func<ToolConfiguration, AITool?>? tools = null,
            IAuditSinkPort? auditSink = null)
        {
            // ConversationObservers.Standard takes a required sink, because the composition root resolves
            // providers.audit for every host and falls back to the in-process memory kind. An optional
            // parameter has to be a compile-time constant, so the default is spelled here instead — a fact
            // that does not care where its events land gets a fresh in-memory sink.
            IAuditSinkPort sink = auditSink ?? new InMemoryAuditSink();

            AgentCoreConfiguration document = ConfigurationLoader.LoadYaml(yaml);
            FakeChatClientFactory chatClients = new(reply);
            CompiledAgent compiled = ConfigurationCompiler.CompileAll(
                document,
                new AgentCompilationContext(chatClients)
                {
                    Tools = TestToolRegistry.From(document, tools, TestContext.Current.CancellationToken),
                })["main"];

            ConversationSessionFactory factory = new(
                compiled,
                new GuardEvaluator(compiled.Configuration.Guards),
                ConversationSessionFactory.CreateExtractor(compiled, chatClients),
                observers: ConversationObservers.Standard(sink, logger: null));

            return factory.Create();
        }
    }
}
