namespace AgentCore.Application.Conversation.Actions
{
    /// <summary>
    /// Hands a phone call to another line. It waits until the caller heard the current answer, because a transfer that
    /// cuts the reply loses what the caller was just told.
    /// </summary>
    public sealed record TransferAction : ConversationAction
    {
        /// <summary>Checks the target here, so a bad target fails in the tool that made it and not later on the call.</summary>
        /// <param name="target">A <c>tel:</c>, <c>sip:</c> or <c>sips:</c> URI, the forms a SIP REFER can carry.</param>
        /// <exception cref="ArgumentNullException"><paramref name="target"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException"><paramref name="target"/> is not an absolute tel, sip or sips URI.</exception>
        public TransferAction(Uri target)
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
        /// Gets the instruction the voice follows when the line did not take the call, after which the call goes on. The
        /// host writes it, because AgentCore writes no text for the model. Without it a failed transfer hangs up. A voice
        /// may keep it for the rest of the call (GPT-Live appends it to its session instructions, as it does the
        /// greeting), so word it as one past event, such as "the transfer you just started did not go through".
        /// </summary>
        public string? IfFailed { get; init; }
    }
}
