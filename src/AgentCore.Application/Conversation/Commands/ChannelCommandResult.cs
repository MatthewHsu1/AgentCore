namespace AgentCore.Application.Conversation.Commands
{
    /// <summary>
    /// The answer to a command.
    /// </summary>
    public enum ChannelCommandResult
    {
        /// <summary>Accepted. It runs when its command says, such as after the current answer was delivered.</summary>
        Scheduled = 0,

        /// <summary>The channel has no way to do it, such as a web chat asked to transfer a call. The tool picks the fallback.</summary>
        NotSupported = 1,

        /// <summary>Refused: an end was already asked for, and a later command would contradict it.</summary>
        Ending = 2,
    }
}
