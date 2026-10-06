namespace AgentCore.Application.Conversation.Commands
{
    /// <summary>
    /// Hands a phone call to another line.
    /// </summary>
    public sealed record TransferCommand : ChannelCommand
    {
        /// <summary>Checks the target here, so a bad target fails in the tool that made it and not later on the call.</summary>
        /// <param name="target">A <c>tel:</c>, <c>sip:</c> or <c>sips:</c> URI, the forms a SIP REFER can carry.</param>
        /// <exception cref="ArgumentNullException"><paramref name="target"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="target"/> is not an absolute tel, sip or sips URI.</exception>
        public TransferCommand(Uri target)
        {
            ArgumentNullException.ThrowIfNull(target);
            if (!target.IsAbsoluteUri || target.Scheme is not ("tel" or "sip" or "sips"))
            {
                throw new ArgumentException($"A transfer target must be a tel:, sip: or sips: URI, not '{target}'.", nameof(target));
            }

            Target = target;
        }

        /// <summary>Gets the target. It is a URI and not a phone number, so a PSTN number and a SIP endpoint both fit.</summary>
        public Uri Target { get; }

        /// <summary>
        /// Gets the instruction the voice follows when the line did not take the call, after which the call goes on.
        /// </summary>
        public string? IfFailed { get; init; }
    }
}
