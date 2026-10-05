namespace AgentCore.AspNetCore.Voice.Ports
{
    /// <summary>Reads one reassembled inbound message into a <see cref="FrameOutcome"/>.</summary>
    /// <param name="utf8">The whole message, already reassembled.</param>
    /// <returns>What the adapter's own reader made of those bytes.</returns>
    internal delegate FrameOutcome FrameParser(ReadOnlySpan<byte> utf8);
}
