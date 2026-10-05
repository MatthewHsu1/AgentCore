using Microsoft.Extensions.AI;

namespace AgentCore.Application.Hooks.Gates
{
    /// <summary>A model round trip is about to start. A hook may change what the model sees, or answer for it.</summary>
    public sealed class ModelGate : HookGate
    {
        internal ModelGate(
            HookScope scope,
            int round,
            IReadOnlyList<ChatMessage> messages,
            ChatOptions? options,
            IDictionary<string, object?> items)
            : base(scope)
        {
            ArgumentNullException.ThrowIfNull(messages);
            ArgumentNullException.ThrowIfNull(items);

            Round = round;
            Messages = messages;
            Options = options;
            Items = items;
        }

        /// <summary>Gets the zero-based round trip of this run.</summary>
        public int Round { get; }

        /// <summary>Gets the messages the model will see, as the hook before this one left them.</summary>
        public IReadOnlyList<ChatMessage> Messages { get; }

        /// <summary>Gets the options of the call, as the hook before this one left them.</summary>
        public ChatOptions? Options { get; }

        /// <summary>Gets the turn's shared state.</summary>
        public IDictionary<string, object?> Items { get; }

        internal IReadOnlyList<ChatMessage>? NewMessages { get; private set; }

        internal ChatOptions? NewOptions { get; private set; }

        internal ChatResponse? Response { get; private set; }

        /// <summary>Sends the model these messages instead.</summary>
        /// <param name="messages">The messages the model sees.</param>
        public void ReplaceMessages(IEnumerable<ChatMessage> messages)
        {
            ArgumentNullException.ThrowIfNull(messages);
            List<ChatMessage> copy = [.. messages];
            Stage(terminal: false, () => NewMessages = copy);
        }

        /// <summary>Calls the model with these options instead.</summary>
        /// <param name="options">The options of the call.</param>
        public void SetOptions(ChatOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            Stage(terminal: false, () => NewOptions = options);
        }

        /// <summary>Does not call the model. This response stands in for its answer. Terminal.</summary>
        /// <param name="response">The response the round trip returns.</param>
        public void Respond(ChatResponse response)
        {
            ArgumentNullException.ThrowIfNull(response);
            Stage(terminal: true, () => Response = response);
        }
    }
}
