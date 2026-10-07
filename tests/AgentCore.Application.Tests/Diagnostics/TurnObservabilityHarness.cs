using AgentCore.TestSupport;
using AgentCore.Application.Audit.Memory;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Diagnostics;
using AgentCore.Application.Hooks;
using AgentCore.Application.Ports;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using Xunit;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.Application.Tests.Diagnostics
{
    /// <summary>
    /// The configurations, the session builder, and the span listener that the tests of what a turn reports share.
    /// </summary>
    internal static class TurnObservabilityHarness
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

        /// <summary>Copies what the listener has collected so far.</summary>
        /// <param name="spans">The list the listener writes into.</param>
        /// <returns>The copy, which no other thread can change.</returns>
        internal static List<Activity> Snapshot(List<Activity> spans)
        {
            lock (spans)
            {
                return [.. spans];
            }
        }

        /// <summary>Subscribes to the one activity source of this library.</summary>
        internal static ActivityListener ListenTo(List<Activity> spans)
        {
            ActivityListener listener = new()
            {
                ShouldListenTo = source =>
                    string.Equals(source.Name, AgentCoreTelemetry.ActivitySourceName, StringComparison.Ordinal),
                Sample = (ref _) => ActivitySamplingResult.AllData,
                ActivityStopped = activity =>
                {
                    lock (spans)
                    {
                        spans.Add(activity);
                    }
                },
            };

            ActivitySource.AddActivityListener(listener);
            return listener;
        }

        internal static ConversationSessionFactory Build(
            string yaml,
            IChatClient reply,
            IChatClient? fill,
            Func<ToolConfiguration, AITool?>? tools = null,
            TimeProvider? timeProvider = null,
            IAuditSinkPort? auditSink = null,
            ILogger? logger = null)
        {
            // The built-in hooks require a sink. A test that does not read its events gets a fresh in-memory one.
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
                })["main"];

            return new ConversationSessionFactory(
                compiled,
                new GuardEvaluator(compiled.Configuration.Guards),
                ConversationSessionFactory.CreateExtractor(compiled, chatClients),
                timeProvider,
                logger,
                hooks: BuiltInHooks.Create(sink, logger));
        }
    }
}
