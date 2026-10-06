namespace AgentCore.Application.Conversation.Commands
{
    /// <summary>
    /// A fact for a voice that speaks for itself, for example a note a host looked up after the call was answered.
    /// </summary>
    public sealed record AddVoiceContextCommand : ChannelCommand
    {
        private const int MaxLength = 1000;

        /// <summary>Keeps the fact, cut to its first 1,000 characters.</summary>
        /// <param name="text">The fact, as the voice should read it.</param>
        /// <exception cref="ArgumentException"><paramref name="text"/> is <see langword="null"/>, empty, or white space.</exception>
        public AddVoiceContextCommand(string text)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(text);
            Text = text.Length > MaxLength ? text[..MaxLength] : text;
        }

        /// <summary>Gets the fact.</summary>
        public string Text { get; }
    }
}
