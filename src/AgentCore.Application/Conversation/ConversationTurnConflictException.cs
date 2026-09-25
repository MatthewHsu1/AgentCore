namespace AgentCore.Application.Conversation
{
    /// <summary>
    /// The store refused a turn's words because the conversation already saved that turn: another session of
    /// the same conversation ran the same turn at the same time, and its commit landed first. Nothing of the
    /// refused append was written.
    /// </summary>
    public sealed class ConversationTurnConflictException : InvalidOperationException
    {
        private const string DefaultMessage = "The conversation already saved this turn, so the store refused it.";

        /// <summary>Creates the exception with its default message.</summary>
        public ConversationTurnConflictException()
            : base(DefaultMessage)
        {
        }

        /// <summary>Creates the exception with a plain message.</summary>
        /// <param name="message">The message a human reads.</param>
        public ConversationTurnConflictException(string message)
            : base(message)
        {
        }

        /// <summary>Creates the exception with a plain message and an inner cause.</summary>
        /// <param name="message">The message a human reads.</param>
        /// <param name="innerException">The cause.</param>
        public ConversationTurnConflictException(string message, Exception? innerException)
            : base(message, innerException)
        {
        }
    }
}
