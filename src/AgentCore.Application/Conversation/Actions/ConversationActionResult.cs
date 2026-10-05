namespace AgentCore.Application.Conversation.Actions
{
    /// <summary>
    /// The answer to an action request. It is a value and not an exception, so the tool can turn a refusal into words
    /// for the model; AgentCore writes no text for the model.
    /// </summary>
    public enum ConversationActionResult
    {
        /// <summary>Accepted. It runs after the current answer was delivered, so a caller hears the answer first.</summary>
        Scheduled = 0,

        /// <summary>The channel has no way to do it, such as a web chat asked to transfer a call. The tool picks the fallback.</summary>
        NotSupported = 1,

        /// <summary>Refused: an end was already asked for, and a later action would contradict it.</summary>
        Ending = 2,
    }
}
