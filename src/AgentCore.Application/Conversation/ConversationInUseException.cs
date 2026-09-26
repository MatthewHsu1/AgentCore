namespace AgentCore.Application.Conversation
{
    /// <summary>
    /// The session owner refused to open a conversation because another entry holds it live, or is opening it.
    /// </summary>
    public sealed class ConversationInUseException : InvalidOperationException
    {
        private const string DefaultMessage = "Another entry holds this conversation live, so the open was refused.";

        /// <summary>Creates the exception with its default message.</summary>
        public ConversationInUseException()
            : base(DefaultMessage)
        {
        }

        /// <summary>Creates the exception with a plain message.</summary>
        /// <param name="message">The message a human reads.</param>
        public ConversationInUseException(string message)
            : base(message)
        {
        }

        /// <summary>Creates the exception with a plain message and an inner cause.</summary>
        /// <param name="message">The message a human reads.</param>
        /// <param name="innerException">The cause.</param>
        public ConversationInUseException(string message, Exception? innerException)
            : base(message, innerException)
        {
        }

        /// <summary>Creates the exception for one refused open.</summary>
        /// <param name="conversationId">The id of the conversation that is held.</param>
        /// <param name="holdingEntry">The entry that holds it live.</param>
        /// <param name="refusedEntry">The entry whose open was refused.</param>
        public ConversationInUseException(string conversationId, string holdingEntry, string refusedEntry)
            : base($"Entry '{holdingEntry}' holds conversation '{conversationId}' live, so entry '{refusedEntry}' was refused.")
        {
            ConversationId = conversationId;
            HoldingEntry = holdingEntry;
        }

        /// <summary>Gets the id of the conversation that is held, or <see langword="null"/> when the thrower named none.</summary>
        public string? ConversationId { get; }

        /// <summary>Gets the entry that holds the conversation live, or <see langword="null"/> when the thrower named none.</summary>
        public string? HoldingEntry { get; }
    }
}
