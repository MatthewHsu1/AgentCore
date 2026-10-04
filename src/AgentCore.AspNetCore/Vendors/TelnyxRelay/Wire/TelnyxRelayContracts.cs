namespace AgentCore.AspNetCore.Vendors.TelnyxRelay.Wire
{
    /// <summary>
    /// One frame of the Telnyx Conversation Relay socket, inbound.
    /// </summary>
    internal abstract record RelayFrame
    {
        /// <summary>The first frame of a conversation. It names every id the conversation will need.</summary>
        /// <param name="SessionId">The relay session.</param>
        /// <param name="CallSid">The conversation, in the vendor vocabulary.</param>
        /// <param name="CallControlId">
        /// The handle the Conversation Control API accepts. A conference warm transfer needs it,
        /// and this frame is the only place it reaches the socket.
        /// </param>
        /// <param name="ConversationSessionId">
        /// The group of legs that belong to one logical conversation. It becomes the AgentCore conversation id,
        /// because it survives a transfer and a single leg id does not.
        /// </param>
        /// <param name="From">The caller number.</param>
        /// <param name="To">The number the caller dialled.</param>
        /// <param name="CustomParameters">Per-conversation values the host attached, or null.</param>
        internal sealed record Setup(
            string SessionId,
            string CallSid,
            string CallControlId,
            string ConversationSessionId,
            string From,
            string To,
            IReadOnlyDictionary<string, string>? CustomParameters) : RelayFrame;

        /// <summary>The caller spoke.</summary>
        /// <param name="VoicePrompt">What the recognizer heard.</param>
        /// <param name="Lang">The language it reported, for example <c>en</c>.</param>
        /// <param name="Last">
        /// <see langword="true"/> marks the end of the turn. <see langword="false"/> is an interim
        /// transcript, and the relay sends many of those. The connection drops every interim frame,
        /// because one turn for each partial word would run the model many times over.
        /// </param>
        internal sealed record Prompt(string VoicePrompt, string Lang, bool Last) : RelayFrame;

        /// <summary>The caller spoke over the reply. THIS IS THE TRUNCATION RECORD.</summary>
        /// <param name="UtteranceUntilInterrupt">The text the caller actually heard.</param>
        /// <param name="DurationUntilInterruptMs">
        /// How much of the reply played, in milliseconds, as the vendor measured it. Neither value is
        /// estimated.
        /// </param>
        internal sealed record Interrupt(string UtteranceUntilInterrupt, int DurationUntilInterruptMs) : RelayFrame;

        /// <summary>The caller pressed one key.</summary>
        /// <param name="Digit">The key. The wire field is singular.</param>
        internal sealed record Dtmf(string Digit) : RelayFrame;

        /// <summary>The vendor refused a frame this application sent.</summary>
        /// <param name="Description">Why it refused. This reports our defect, not a conversation fault.</param>
        internal sealed record Error(string Description) : RelayFrame;
    }

    /// <summary>One piece of the reply, outbound.</summary>
    /// <param name="Token">The text to speak.</param>
    /// <param name="Last">Whether this piece closes the reply.</param>
    internal sealed record RelayToken(string Token, bool Last)
    {
        /// <summary>Gets the wire discriminator.</summary>
        public string Type { get; } = "text";
    }

    /// <summary>Hands the conversation back to the vendor, outbound.</summary>
    /// <param name="HandoffData">Anything the next step should read, or null.</param>
    internal sealed record RelayEnd(string? HandoffData)
    {
        /// <summary>Gets the wire discriminator.</summary>
        public string Type { get; } = "end";
    }
}
