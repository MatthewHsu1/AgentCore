namespace AgentCore.AspNetCore.Voice.Ports
{
    /// <summary>What reading one inbound message produced.</summary>
    /// <param name="Frame">The parsed frame, or <see langword="null"/> when none was read.</param>
    /// <param name="UnknownType">The discriminator no case matched, or <see langword="null"/>.</param>
    /// <param name="RefusedType">The known discriminator whose body would not bind, or <see langword="null"/>.</param>
    internal readonly record struct FrameOutcome(object? Frame, string? UnknownType, string? RefusedType);
}
