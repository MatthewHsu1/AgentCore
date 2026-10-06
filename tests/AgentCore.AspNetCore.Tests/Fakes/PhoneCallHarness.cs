using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Conversation.Memory;
using AgentCore.Application.Hooks;
using AgentCore.Application.Hooks.BuiltIn;
using AgentCore.Application.Hooks.Layers;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions.Memory;
using AgentCore.AspNetCore.Calls;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.AspNetCore.Tests.Fakes
{
    /// <summary>Real sessions and compiled hooks for <see cref="PhoneCall"/>, over a fake model.</summary>
    internal sealed class PhoneCallHarness : IDisposable
    {
        internal const string OneEntryYaml = """
        apiVersion: agentcore/v1
        tools:
          - { id: price_lookup, kind: builtin, uses: orders.read, description: "Look up the price of an item." }
        agents:
          defaults: { clock: false }
          items:
            - { id: only, instructions: "answer the caller", tools: [ price_lookup ] }
        entries:
          main:
            agent: only
          other:
            agent: only
        """;

        private PhoneCallHarness(InMemoryConversationSessions sessions, PhoneCallHost host, CallBriefHook briefs)
        {
            Sessions = sessions;
            Host = host;
            Briefs = briefs;
        }

        public InMemoryConversationSessions Sessions { get; }

        public PhoneCallHost Host { get; }

        public CallBriefHook Briefs { get; }

        public static PhoneCallHarness Create(
            IChatClient model,
            IReadOnlyList<AgentHook> hooks,
            string yaml = OneEntryYaml,
            TimeProvider? time = null,
            IConversationStore? store = null,
            Func<ToolConfiguration, AITool?>? tools = null)
        {
            TimeProvider clock = time ?? TimeProvider.System;
            CallBriefHook briefs = new();
            AgentCoreConfiguration document = ConfigurationLoader.LoadYaml(yaml);
            IReadOnlyDictionary<string, CompiledAgent> compiled = ConfigurationCompiler.CompileAll(
                document,
                new AgentCompilationContext(new RoutingChatClientFactory(model))
                {
                    ConversationStore = store ?? new InMemoryConversationStore(clock),
                    Tools = TestToolRegistry.From(
                        document,
                        tools ?? (tool => AIFunctionFactory.Create(() => "{\"price\":42}", tool.Id, tool.Description ?? tool.Id)),
                        TestContext.Current.CancellationToken),
                    Hooks = [briefs, .. hooks],
                    Clock = clock,
                });

            Dictionary<string, IConversationSessionFactory> factories = new(StringComparer.Ordinal);
            foreach ((string entry, CompiledAgent agent) in compiled)
            {
                factories[entry] = new ConversationSessionFactory(agent, new GuardEvaluator(agent.Configuration.Guards), timeProvider: clock);
            }

            InMemoryConversationSessions sessions = new(factories, TimeSpan.FromMinutes(5), clock);
            PhoneCallHost host = new(sessions, compiled[SingleEntrySessionFactories.MainEntry].Hooks, clock, NullLogger.Instance, GatePoint.BeforeCall.Deadline);
            return new PhoneCallHarness(sessions, host, briefs);
        }

        public static CallOffer Offer(string callId = "call-7", string entry = SingleEntrySessionFactories.MainEntry)
        {
            return new CallOffer(
                entry, callId, "+15550100", "+15550199",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Diversion"] = "<sip:+15550111@goto>" },
                "test-transport");
        }

        public void Dispose()
        {
            Sessions.Dispose();
        }
    }
}
