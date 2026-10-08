namespace AgentCore.Application.Conversation.Commands
{
    /// <summary>
    /// The plan a voice that speaks for itself runs between the backend's answers: the next step, what to ask or watch
    /// for, and when to delegate again. Each plan replaces the one before it.
    /// </summary>
    public sealed record SetVoicePlanCommand : ChannelCommand
    {
        /// <summary>
        /// The longest plan, in characters. One GPT-Live append holds 500 tokens; 1,000 characters leaves room for the
        /// channel's own line before the plan, even at about 3 characters per token.
        /// </summary>
        public const int MaxLength = 1000;

        /// <summary>Keeps the plan.</summary>
        /// <param name="text">The plan, as the voice should read it.</param>
        /// <exception cref="ArgumentException">
        /// <paramref name="text"/> is <see langword="null"/>, empty, white space, or longer than <see cref="MaxLength"/>.
        /// </exception>
        public SetVoicePlanCommand(string text)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(text);
            if (text.Length > MaxLength)
            {
                throw new ArgumentException($"The plan is {text.Length} characters. The limit is {MaxLength}.", nameof(text));
            }

            Text = text;
        }

        /// <summary>Gets the plan.</summary>
        public string Text { get; }
    }
}
