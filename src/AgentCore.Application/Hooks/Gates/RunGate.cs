using Microsoft.Extensions.AI;

namespace AgentCore.Application.Hooks.Gates
{
    /// <summary>A run's context is being built. A hook may add instructions, messages, or tools.</summary>
    public sealed class RunGate : HookGate
    {
        private readonly List<string> _instructions = [];

        private readonly List<ChatMessage> _messages = [];

        private readonly List<AITool> _tools = [];

        internal RunGate(HookScope scope, string agentId, bool nested, IDictionary<string, object?> items)
            : base(scope)
        {
            ArgumentNullException.ThrowIfNull(agentId);
            ArgumentNullException.ThrowIfNull(items);

            AgentId = agentId;
            Nested = nested;
            Items = items;
        }

        /// <summary>Gets the agent about to run.</summary>
        public string AgentId { get; }

        /// <summary>Gets whether this run belongs to a graph participant or an agent-as-tool child.</summary>
        public bool Nested { get; }

        /// <summary>Gets the turn's shared state.</summary>
        public IDictionary<string, object?> Items { get; }

        internal IReadOnlyList<string> Instructions => _instructions;

        internal IReadOnlyList<ChatMessage> Messages => _messages;

        internal IReadOnlyList<AITool> Tools => _tools;

        /// <summary>Adds text to this run's instructions.</summary>
        /// <param name="text">The instructions to add.</param>
        public void AddInstructions(string text)
        {
            ArgumentNullException.ThrowIfNull(text);
            Stage(terminal: false, () => _instructions.Add(text));
        }

        /// <summary>Adds messages to this run's context.</summary>
        /// <param name="messages">The messages to add.</param>
        public void AddMessages(IEnumerable<ChatMessage> messages)
        {
            ArgumentNullException.ThrowIfNull(messages);
            List<ChatMessage> copy = [.. messages];
            Stage(terminal: false, () => _messages.AddRange(copy));
        }

        /// <summary>Adds tools to this run.</summary>
        /// <param name="tools">The tools to add.</param>
        public void AddTools(IEnumerable<AITool> tools)
        {
            ArgumentNullException.ThrowIfNull(tools);
            List<AITool> copy = [.. tools];
            Stage(terminal: false, () => _tools.AddRange(copy));
        }
    }
}
