using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Hooks;
using AgentCore.Application.Ports;
using AgentCore.Application.Skills;
using AgentCore.Application.Tests.Fakes;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.Application.Tests.Hooks
{
    /// <summary>Builds sessions whose agents are compiled with hooks.</summary>
    internal static class HookSessions
    {
        internal const string OneAgentYaml = """
        apiVersion: agentcore/v1
        agents:
          defaults: { clock: false }
          items:
            - { id: only, instructions: "ok" }
        entries:
          main:
            agent: only
        """;

        internal const string TwoEntryYaml = """
        apiVersion: agentcore/v1
        agents:
          defaults: { clock: false }
          items:
            - { id: only, instructions: "ok" }
        entries:
          main:
            agent: only
          other:
            agent: only
        """;

        internal const string ToolAgentYaml = """
        apiVersion: agentcore/v1
        tools:
          - { id: price_lookup, kind: builtin, uses: orders.read, description: "Look up the price of an item." }
        agents:
          defaults: { clock: false }
          items:
            - { id: only, instructions: "quote the price", tools: [ price_lookup ] }
        entries:
          main:
            agent: only
        """;

        internal const string EndsAfterTheFirstTurnYaml = """
        apiVersion: agentcore/v1
        guards:
          afterFirst: { ">=": [ { var: turnIndex }, 1 ] }
        agents:
          defaults: { clock: false }
          items:
            - { id: only, instructions: "ok" }
        entries:
          main:
            policy:
              initial: working
              stages:
                - { id: working, agent: only, to: [ { stage: done, when: afterFirst } ] }
                - { id: done, agent: only, terminal: true }
        """;

        internal const string DelegatingYaml = """
        apiVersion: agentcore/v1
        tools:
          - { id: ask_helper, kind: agent, agent: helper, description: Ask the helper. }
        agents:
          defaults: { clock: false }
          items:
            - { id: only, instructions: "delegate", tools: [ ask_helper ] }
            - { id: helper, instructions: "help" }
        entries:
          main:
            agent: only
        """;

        internal const string GraphYaml = """
        apiVersion: agentcore/v1
        tools:
          - { id: price_lookup, kind: builtin, uses: orders.read, description: "Look up the price of an item." }
        agents:
          defaults: { clock: false }
          items:
            - { id: researcher, instructions: "look it up", tools: [ price_lookup ] }
            - { id: responder, instructions: "answer" }
        entries:
          main:
            graph:
              pattern: sequential
              agents: [ researcher, responder ]
        """;

        /// <summary>A graph row that keeps one workflow session for the whole conversation (a harness switch is set).</summary>
        internal const string KeptGraphYaml = """
        apiVersion: agentcore/v1
        agents:
          defaults: { clock: false }
          items:
            - { id: researcher, instructions: "look it up", todos: true }
            - { id: responder, instructions: "answer" }
        entries:
          main:
            graph:
              pattern: sequential
              agents: [ researcher, responder ]
        """;

        internal const string ExplicitGraphYaml = """
        apiVersion: agentcore/v1
        agents:
          defaults: { clock: false }
          items:
            - { id: researcher, instructions: "look it up" }
            - { id: responder, instructions: "answer" }
        entries:
          main:
            graph:
              nodes:
                - { id: research, agent: researcher, start: true }
                - { id: respond, agent: responder, output: true }
              edges:
                - { from: research, to: respond }
        """;

        internal const string ConcurrentGraphYaml = """
        apiVersion: agentcore/v1
        agents:
          defaults: { clock: false }
          items:
            - { id: researcher, instructions: "look it up" }
            - { id: responder, instructions: "answer" }
        entries:
          main:
            graph:
              pattern: concurrent
              agents: [ researcher, responder ]
        """;

        internal const string HandoffGraphYaml = """
        apiVersion: agentcore/v1
        agents:
          defaults: { clock: false }
          items:
            - { id: researcher, instructions: "hand off" }
            - { id: responder, instructions: "answer" }
        entries:
          main:
            graph:
              pattern: handoff
              agents: [ researcher, responder ]
        """;

        internal const string BackgroundYaml = """
        apiVersion: agentcore/v1
        agents:
          defaults: { clock: false }
          items:
            - { id: parent, instructions: "delegate work", model: { ref: parent }, background: [blocker] }
            - { id: blocker, instructions: "work forever", model: { ref: blocker } }
        entries:
          main:
            agent: parent
        """;

        internal static IReadOnlyDictionary<string, CompiledAgent> Compile(
            string yaml,
            IChatClient model,
            IReadOnlyList<AgentHook>? hooks = null,
            IConversationStore? store = null,
            Func<ToolConfiguration, AITool?>? tools = null,
            TimeProvider? time = null,
            SkillCatalog? skills = null)
        {
            AgentCoreConfiguration document = ConfigurationLoader.LoadYaml(yaml);
            return ConfigurationCompiler.CompileAll(
                document,
                new AgentCompilationContext(new FakeChatClientFactory(model))
                {
                    ConversationStore = store,
                    Tools = TestToolRegistry.From(document, tools, TestContext.Current.CancellationToken),
                    Hooks = hooks,
                    Clock = time,
                    Skills = skills,
                });
        }

        internal static ConversationSession Create(
            string yaml,
            IChatClient model,
            IReadOnlyList<AgentHook>? hooks = null,
            IConversationStore? store = null,
            Func<ToolConfiguration, AITool?>? tools = null,
            string? conversationId = null,
            TimeProvider? time = null,
            SkillCatalog? skills = null)
        {
            CompiledAgent compiled = Compile(yaml, model, hooks, store, tools, time, skills)["main"];
            return new ConversationSessionFactory(compiled, new GuardEvaluator(compiled.Configuration.Guards), timeProvider: time)
                .Create(conversationId);
        }
    }
}
