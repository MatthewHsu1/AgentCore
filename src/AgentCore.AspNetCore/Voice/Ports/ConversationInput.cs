namespace AgentCore.AspNetCore.Voice.Ports
{
    /// <summary>
    /// One thing the caller did, in the order it happened.
    /// </summary>
    public abstract record ConversationInput
    {
        private protected ConversationInput()
        {
        }

        /// <summary>The conversation began. It comes first, before anything the caller did.</summary>
        /// <param name="ConversationId">
        /// The id the transport names this conversation by. A second one on the same stream replaces the first.
        /// </param>
        public sealed record Started(string ConversationId) : ConversationInput
        {
            /// <summary>Gets the caller's number, when the transport names it.</summary>
            public string? From { get; init; }

            /// <summary>Gets the number the caller dialled, when the transport names it.</summary>
            public string? To { get; init; }

            /// <summary>Gets the transport's per-call values, such as the relay's custom parameters, or <see langword="null"/>.</summary>
            public IReadOnlyDictionary<string, string>? Headers { get; init; }
        }

        /// <summary>The caller spoke.</summary>
        /// <param name="Text">What the recognizer heard.</param>
        /// <param name="Language">The language it reported, for example <c>en</c>.</param>
        /// <param name="IsFinal">
        /// <see langword="true"/> marks the end of the turn. <see langword="false"/> is an interim
        /// transcript, and a recognizer sends many of those for one sentence.
        /// </param>
        public sealed record Utterance(string Text, string Language, bool IsFinal) : ConversationInput;

        /// <summary>The caller pressed one key.</summary>
        /// <param name="Key">The key. A keypad carries card numbers and PINs, so no log line may hold it.</param>
        public sealed record Keypress(string Key) : ConversationInput;

        /// <summary>The caller cut the reply off.</summary>
        /// <param name="HeardText">
        /// The text the caller actually heard. An empty string means the caller heard nothing at all,
        /// which is a measured answer and not a missing one: a barge-in can land before the first word
        /// leaves the synthesizer. A consumer must not record an assistant turn for an empty value.
        /// </param>
        /// <param name="PlayedDuration">
        /// How much of the reply played. Measured, never estimated.
        /// </param>
        public sealed record Barge(string HeardText, TimeSpan PlayedDuration) : ConversationInput;
    }
}
