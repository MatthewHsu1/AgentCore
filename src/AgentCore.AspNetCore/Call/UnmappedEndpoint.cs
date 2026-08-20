using Microsoft.AspNetCore.Builder;

namespace AgentCore.AspNetCore.Call;

/// <summary>
/// What a host gets back from <c>MapCall</c> when no route was mapped.
/// </summary>
/// <remarks>
/// <para>
/// Three reasons reach it, and none of them is a failure: the host registered no call adapter at
/// all, the host loaded no <c>providers.call</c> block, or the block named a vendor this process
/// dials <b>out</b> to, which owns no inbound URL.
/// </para>
/// <para>
/// <c>MapCall</c> must hand something back, and there is no endpoint to hand back: nothing was
/// mapped. This builds over no endpoint, so a convention a host adds to it is applied to nothing at
/// all rather than to some other route. It is shared, because it holds no state and keeps none of
/// what it is given.
/// </para>
/// </remarks>
internal sealed class UnmappedEndpoint : IEndpointConventionBuilder
{
    /// <summary>Gets the one instance. Nothing here is per-call or per-route.</summary>
    public static UnmappedEndpoint Instance { get; } = new();

    /// <summary>Drops the convention, because there is no endpoint to apply it to.</summary>
    /// <param name="convention">What the host would have applied.</param>
    public void Add(Action<EndpointBuilder> convention)
    {
        // Nothing was mapped, so there is nothing to configure.
    }
}
