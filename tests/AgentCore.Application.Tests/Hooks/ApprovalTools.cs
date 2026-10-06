using System.Collections.Concurrent;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Hooks;
using AgentCore.Application.Ports;
using AgentCore.Application.Tests.Fakes;
using Microsoft.Extensions.AI;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.Application.Tests.Hooks
{
    /// <summary>The tools the approval-hook tests offer, each counting its runs: three that need approval and one that does not.</summary>
    internal sealed class ApprovalTools
    {
        /// <summary>One agent with <c>send_email</c>, which needs approval.</summary>
        internal const string GatedYaml = """
        apiVersion: agentcore/v1
        tools:
          - { id: send_email, kind: builtin, uses: test.send, description: "Send an email." }
        agents:
          items:
            - id: only
              instructions: "send the mail"
              tools: [ send_email ]
        entries:
          main:
            agent: only
        """;

        private readonly ConcurrentDictionary<string, int> _ran = new(StringComparer.Ordinal);

        /// <summary>Gets how many times <c>send_email</c> ran.</summary>
        public int Sent => Ran("send_email");

        /// <summary>Gets the model most tests use: it calls the first tool once, under the id <c>conversation_1</c>, then says "done.".</summary>
        public ToolCallingChatClient Model { get; } = new("done.", new Dictionary<string, object?>(StringComparer.Ordinal) { ["to"] = "a@b.com" });

        /// <summary>Gets how many times the named tool ran.</summary>
        public int Ran(string tool)
        {
            return _ran.TryGetValue(tool, out int count) ? count : 0;
        }

        public ConversationSession Session(
            string yaml,
            IReadOnlyList<AgentHook> hooks,
            IChatClient? model = null,
            IConversationStore? store = null)
        {
            return HookSessions.Create(yaml, model ?? Model, hooks, store: store, tools: Tool);
        }

        public AIFunction? Tool(ToolConfiguration declared)
        {
            return declared.Uses switch
            {
                "test.send" => new ApprovalRequiredAIFunction(Counting("send_email")),
                "test.wire" => new ApprovalRequiredAIFunction(Counting("wire_money")),
                "test.archive" => new ApprovalRequiredAIFunction(Counting("archive")),
                "orders.read" => Counting("price_lookup"),
                _ => null,
            };
        }

        public ApprovalRequiredAIFunction Send()
        {
            return new(Counting("send_email"));
        }

        private AIFunction Counting(string name)
        {
            return AIFunctionFactory.Create(
                () =>
                {
                    _ = _ran.AddOrUpdate(name, 1, static (_, count) => count + 1);
                    return "ok";
                },
                name,
                "A test tool.");
        }
    }
}
