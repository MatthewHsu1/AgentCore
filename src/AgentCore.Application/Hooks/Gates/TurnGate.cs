using AgentCore.Application.Hooks.Notices;

namespace AgentCore.Application.Hooks.Gates
{
    /// <summary>The user's message arrived, before moderation and the model. A hook may block it, rewrite it, or add context.</summary>
    public sealed class TurnGate : HookGate
    {
        private readonly List<string> _addedContext = [];

        internal TurnGate(HookScope scope, string userText, IDictionary<string, object?> items)
            : base(scope)
        {
            ArgumentNullException.ThrowIfNull(userText);
            ArgumentNullException.ThrowIfNull(items);

            UserText = userText;
            Items = items;
        }

        /// <summary>Gets the user's text, after any earlier hook's <see cref="ReplaceInput"/>.</summary>
        public string UserText { get; }

        /// <summary>Gets the turn's shared state.</summary>
        public IDictionary<string, object?> Items { get; }

        internal string? BlockReply { get; private set; }

        internal string? Input { get; private set; }

        internal IReadOnlyList<string> AddedContext => _addedContext;

        /// <summary>Does not run the turn. It is committed with <c>Outcome = Blocked</c> and <paramref name="reply"/> as the answer. Terminal.</summary>
        /// <param name="reply">The text the caller gets.</param>
        public void Block(string reply)
        {
            ArgumentNullException.ThrowIfNull(reply);
            Stage(terminal: true, () => BlockReply = reply);
        }

        /// <summary>
        /// Rewrites the user's message. The store, <c>TurnCompleted.UserText</c> and the audit hash all see the new text.
        /// The rewrite is made in place on the turn's user message, the same <see cref="Microsoft.Extensions.AI.ChatMessage"/>
        /// object the host passed in. Its text becomes <paramref name="text"/>; its other contents (an image, an approval
        /// answer) are kept, but may be moved after the text. <see cref="TurnStarted"/> was raised before this gate, so it
        /// keeps the words as received.
        /// </summary>
        /// <param name="text">The message the turn runs on.</param>
        public void ReplaceInput(string text)
        {
            ArgumentNullException.ThrowIfNull(text);
            Stage(terminal: false, () => Input = text);
        }

        /// <summary>Tells the model something for this turn's runs only. It is never stored and is not a user row.</summary>
        /// <param name="text">The context note.</param>
        public void AddContext(string text)
        {
            ArgumentNullException.ThrowIfNull(text);
            Stage(terminal: false, () => _addedContext.Add(text));
        }
    }
}
