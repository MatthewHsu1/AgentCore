namespace AgentCore.Application.Conversation.Commands
{
    /// <summary>
    /// One door for every command.
    /// </summary>
    public interface IChannelControl
    {
        /// <summary>Returns at once and runs nothing inline: each command runs when it says, such as after the current answer was delivered.</summary>
        /// <param name="command">One of the commands AgentCore defines.</param>
        /// <returns>Whether the command will run, and if not, why, so the tool can tell the model.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="command"/> is <see langword="null"/>.</exception>
        ChannelCommandResult Send(ChannelCommand command);
    }
}
