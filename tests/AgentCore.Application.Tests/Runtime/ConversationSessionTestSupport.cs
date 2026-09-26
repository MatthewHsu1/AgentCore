using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Runtime;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentCore.Application.Tests.Runtime
{
    /// <summary>
    /// The YAML documents, the chat-client wiring, and the compare helper the split
    /// <c>ConversationSession</c> test classes share.
    /// </summary>
    internal static class ConversationSessionTestSupport
    {
        internal const string PolicyYaml =
            """
          apiVersion: agentcore/v1
          state:
            callerSaidGoodbye:
              type: boolean
              default: false
              writer: extractor
              description: whether the caller said goodbye
            brand: { type: string, writer: const, value: sole }
            greetingTurns:
              type: integer
              default: 0
              writer: counter
              increment: { "===": [ { var: stage }, "greeting" ] }
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
                  - id: greeting
                    agent: greeter
                    to: [ { stage: close, when: saidGoodbye } ]
                  - id: close
                    agent: closer
                    terminal: true
          """;

        internal const string ReminderYaml =
            """
          apiVersion: agentcore/v1
          state:
            machineModel: { type: string, writer: extractor, description: the machine model }
            serialNumber: { type: string, writer: extractor, description: the serial number }
          guards:
            identified:
              and:
                - { "!!": [ { var: machineModel } ] }
                - { "!!": [ { var: serialNumber } ] }
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
                  - id: greeting
                    agent: greeter
                    to: [ { stage: close, when: identified } ]
                  - id: close
                    agent: closer
                    terminal: true
          """;

        internal const string TwoStagesYaml =
            """
          apiVersion: agentcore/v1
          agents:
            defaults:
              model: { ref: reply }
            items:
              - { id: greeter, instructions: "I am the greeter" }
              - { id: closer,  instructions: "I am the closer" }
          entries:
            main:
              policy:
                initial: greeting
                stages:
                  - { id: greeting, agent: greeter, to: [ { stage: close } ] }
                  - { id: close,    agent: closer,  to: [ { stage: greeting } ] }
          """;

        internal const string ToolYaml =
            """
          apiVersion: agentcore/v1
          state:
            orderStatus:       { type: string,  writer: tool, from: lookup_order.status }
            callerSaidGoodbye: { type: boolean, default: false, writer: extractor }
            shippedTurns:
              type: integer
              default: 0
              writer: counter
              increment: { "===": [ { var: orderStatus }, "shipped" ] }
          guards:
            saidGoodbye: { var: callerSaidGoodbye }
          extractor:
            model: { ref: fill }
            when: after_reply
          tools:
            - { id: lookup_order, kind: builtin, uses: orders.read, description: "Look up an order by its id." }
          agents:
            defaults:
              model: { ref: reply }
            items:
              - { id: greeter, instructions: "greet the caller", tools: [ lookup_order ] }
              - { id: closer,  instructions: "close the conversation" }
          entries:
            main:
              policy:
                initial: greeting
                stages:
                  - { id: greeting, agent: greeter, to: [ { stage: close, when: saidGoodbye } ] }
                  - { id: close,    agent: closer,  terminal: true }
          """;

        internal const string OneAgentYaml =
            """
        apiVersion: agentcore/v1
        agents:
          items:
            - { id: only, instructions: "I answer everything" }
        entries:
          main:
            agent: only
        """;

        internal const string StayingNull = /*lang=json,strict*/ """{ "callerSaidGoodbye": null }""";
        internal const string SaidGoodbye = /*lang=json,strict*/ """{ "callerSaidGoodbye": true }""";

        internal static bool Contains(ChatMessage message, string text)
        {
            return message.Text.Contains(text, StringComparison.Ordinal);
        }

        internal static ConversationSessionFactory Build(
            string yaml,
            IChatClient reply,
            IChatClient? fill,
            Func<ToolConfiguration, AITool?>? tools = null,
            TimeProvider? timeProvider = null,
            ILogger? logger = null)
        {
            CompiledAgent compiled = Compile(yaml, reply, fill, tools);
            RoutingChatClientFactory chatClients = new(reply);
            if (fill is not null)
            {
                _ = chatClients.Route("fill", fill);
            }

            return new ConversationSessionFactory(
                compiled,
                new GuardEvaluator(compiled.Configuration.Guards),
                ConversationSessionFactory.CreateExtractor(compiled, chatClients),
                timeProvider,
                logger);
        }

        internal static CompiledAgent Compile(string yaml, IChatClient reply, IChatClient? fill, Func<ToolConfiguration, AITool?>? tools)
        {
            AgentCoreConfiguration document = ConfigurationLoader.LoadYaml(yaml);
            RoutingChatClientFactory chatClients = new(reply);
            if (fill is not null)
            {
                _ = chatClients.Route("fill", fill);
            }

            return ConfigurationCompiler.CompileAll(
                document,
                new AgentCompilationContext(chatClients)
                {
                    Tools = TestToolRegistry.From(document, tools, TestContext.Current.CancellationToken),
                })["main"];
        }
    }
}
