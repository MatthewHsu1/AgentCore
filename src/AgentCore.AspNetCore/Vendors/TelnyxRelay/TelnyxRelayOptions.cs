namespace AgentCore.AspNetCore.Vendors.TelnyxRelay;

/// <summary>
/// What the relay endpoint may do, and for how long.
/// </summary>
/// <remarks>
/// Every default here is chosen for a phone call. A call is short, it is quiet in bytes, and a
/// dead one must be noticed in seconds rather than minutes.
/// </remarks>
public sealed class TelnyxRelayOptions
{
    /// <summary>Gets or sets the largest inbound frame the endpoint accepts, in bytes.</summary>
    /// <remarks>
    /// The WebSocket middleware turns the request timeout off once it accepts, so nothing else
    /// bounds a frame. A real setup frame is under 2 KB, and 64 KB leaves room for custom
    /// parameters without letting one message exhaust the host.
    /// </remarks>
    public int MaxFrameBytes { get; set; } = 64 * 1024;

    /// <summary>Gets or sets how long teardown gives a stuck task before it moves on.</summary>
    /// <remarks>
    /// Two separate waits share this one bound: how long the last turn of a call may take to
    /// unwind once teardown cancels it, and how long the close handshake may take before the
    /// socket is aborted. A telephony call that already dropped never answers a close frame, and a
    /// chat client is not guaranteed to honour cancellation promptly either, so both waits need a
    /// bound or either one could wedge teardown forever. Because the two run one after the other
    /// rather than together, one wedged call to teardown can spend this value twice — worst case
    /// close to ten seconds at the default — which is longer than Kestrel's own five-second default
    /// shutdown timeout. That trade is intentional: a slow, bounded teardown beats one with no
    /// bound at all.
    /// </remarks>
    public TimeSpan CloseTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Gets or sets how long the endpoint waits with no inbound frame before it ends the call.</summary>
    /// <remarks>
    /// The vendor never reconnects, so a silent socket is a call that is already over. The clock
    /// is a <see cref="TimeProvider"/>, resolved from the request's own service provider rather
    /// than a property of this class: <see cref="DependencyInjection.AgentCoreOptions.TimeProvider"/>
    /// when the host bound one, otherwise <see cref="TimeProvider.System"/>. A test that wants a
    /// deterministic idle deadline binds that same property to a fake clock.
    /// </remarks>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(5);
}
