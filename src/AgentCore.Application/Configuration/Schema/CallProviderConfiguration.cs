namespace AgentCore.Application.Configuration.Schema;

/// <summary>
/// The vendor that carries the call, and the limits of the socket it opens.
/// </summary>
public sealed record CallProviderConfiguration
{
    /// <summary>Gets the vendor that carries the call, such as <c>telnyx-relay</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>Gets how long the socket waits with no inbound frame before ending the call, or null for the adapter's default.</summary>
    public int? IdleTimeoutSeconds { get; init; }

    /// <summary>Gets how long teardown gives a stuck task before moving on, or null for the adapter's default.</summary>
    public int? CloseTimeoutSeconds { get; init; }

    /// <summary>Gets the largest inbound frame the socket accepts, in bytes, or null for the adapter's default.</summary>
    public int? MaxFrameBytes { get; init; }
}
