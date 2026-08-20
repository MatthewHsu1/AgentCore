namespace AgentCore.AspNetCore.Vendors.TelnyxRelay;

/// <summary>
/// One frame of the Telnyx Conversation Relay socket, inbound.
/// </summary>
/// <remarks>
/// <para>
/// Section 7.1 makes this a wire format and not a port. The vendor boundary moved from audio to
/// text with D28, so no frame here carries a buffer, a codec, a sample rate, or a playback clock.
/// Item 6c of section 11 asks for exactly that, and this file is where it holds.
/// </para>
/// <para>
/// One discriminator names the frame, as section 3.2 taught: model the field, not one type for
/// each value. These records stay in the adapter, and D8 keeps them out of the core.
/// </para>
/// </remarks>
internal abstract record RelayFrame
{
    /// <summary>The first frame of a call. It names every id the call will need.</summary>
    /// <param name="SessionId">The relay session.</param>
    /// <param name="CallSid">The call, in the vendor vocabulary.</param>
    /// <param name="CallControlId">
    /// The handle the Call Control API accepts. Slice 2 needs it for the conference warm transfer
    /// of section 4.3, and this frame is the only place it reaches the socket.
    /// </param>
    /// <param name="CallSessionId">
    /// The group of legs that belong to one logical call. It becomes the AgentCore call id,
    /// because it survives a transfer and a single leg id does not.
    /// </param>
    /// <param name="From">The caller number.</param>
    /// <param name="To">The number the caller dialled.</param>
    /// <param name="CustomParameters">Per-call values the host attached, or null.</param>
    internal sealed record Setup(
        string SessionId,
        string CallSid,
        string CallControlId,
        string CallSessionId,
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
    /// estimated, which is the whole of D28. Item 6a of section 11 asks for the first one.
    /// </param>
    internal sealed record Interrupt(string UtteranceUntilInterrupt, int DurationUntilInterruptMs) : RelayFrame;

    /// <summary>The caller pressed one key.</summary>
    /// <param name="Digit">The key. The wire field is singular.</param>
    internal sealed record Dtmf(string Digit) : RelayFrame;

    /// <summary>The vendor refused a frame this application sent.</summary>
    /// <param name="Description">Why it refused. This reports our defect, not a call fault.</param>
    internal sealed record Error(string Description) : RelayFrame;
}

/// <summary>One piece of the reply, outbound.</summary>
/// <param name="Token">The text to speak.</param>
/// <param name="Last">Whether this piece closes the reply.</param>
/// <remarks>
/// The vendor needs the discriminator on every outbound frame, and it counts a frame without one
/// as malformed. Repeated malformed frames make the vendor close the socket, so <see cref="Type"/>
/// is a property and not a caller's responsibility.
/// </remarks>
internal sealed record RelayToken(string Token, bool Last)
{
    /// <summary>Gets the wire discriminator.</summary>
    public string Type { get; } = "text";
}

/// <summary>Hands the call back to the vendor, outbound.</summary>
/// <param name="HandoffData">Anything the next step should read, or null.</param>
internal sealed record RelayEnd(string? HandoffData)
{
    /// <summary>Gets the wire discriminator.</summary>
    public string Type { get; } = "end";
}
