namespace AgentCore.Application.Call;

/// <summary>What one call needs to open its channel.</summary>
/// <param name="CallId">The AgentCore call id, which the channel must not invent for itself.</param>
/// <param name="CustomParameters">Per-call values the host attached, or <see langword="null"/>.</param>
/// <remarks>
/// This carries no vendor field and no wire frame, so D8 holds: a factory learns what the call is
/// without the core learning what the vendor is.
/// </remarks>
public sealed record CallChannelContext(string CallId, IReadOnlyDictionary<string, string>? CustomParameters);
