namespace AgentCore.Application.Conversation.Commands
{
    /// <summary>
    /// A host that carries out one command itself.
    /// </summary>
    /// <typeparam name="TCommand">The command the host carries out.</typeparam>
    /// <typeparam name="TOutcome">What the channel needs to know of how it went, such as <see cref="CallTransferOutcome"/>.</typeparam>
    public interface IChannelCommandHandler<in TCommand, TOutcome>
        where TCommand : ChannelCommand
    {
        /// <summary>Carries out the command. A transfer runs once the caller heard the answer that asked for it.</summary>
        /// <param name="command">The command, as the tool sent it.</param>
        /// <param name="context">The conversation the command acts on.</param>
        /// <param name="cancellationToken">Cancels the command.</param>
        /// <returns>How it went.</returns>
        Task<TOutcome> HandleAsync(TCommand command, ChannelCommandContext context, CancellationToken cancellationToken);
    }
}
