using Microsoft.Extensions.AI;

namespace AgentCore.Application.Hooks.Gates
{
    /// <summary>A model round trip returned, before tools run or text leaves. A hook may replace the response.</summary>
    public sealed class ModelResultGate : HookGate
    {
        internal ModelResultGate(HookScope scope, int round, ChatResponse response, IDictionary<string, object?> items)
            : base(scope)
        {
            ArgumentNullException.ThrowIfNull(response);
            ArgumentNullException.ThrowIfNull(items);

            Round = round;
            Response = response;
            Items = items;
        }

        /// <summary>Gets the zero-based round trip of this run.</summary>
        public int Round { get; }

        /// <summary>Gets the model's response, as the hook before this one left it.</summary>
        public ChatResponse Response { get; }

        /// <summary>Gets the turn's shared state.</summary>
        public IDictionary<string, object?> Items { get; }

        internal ChatResponse? Replacement { get; private set; }

        /// <summary>Uses this response in place of the model's. Replaced arguments or an injected tool call reach the tool loop.</summary>
        /// <param name="response">The response the round trip returns.</param>
        public void ReplaceResponse(ChatResponse response)
        {
            ArgumentNullException.ThrowIfNull(response);
            Stage(terminal: false, () => Replacement = response);
        }
    }
}
