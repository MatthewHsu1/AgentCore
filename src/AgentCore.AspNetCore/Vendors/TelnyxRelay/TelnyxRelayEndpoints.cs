using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;

namespace AgentCore.AspNetCore.Vendors.TelnyxRelay;

/// <summary>
/// Maps the Telnyx Conversation Relay socket onto the turn loop.
/// </summary>
/// <remarks>
/// <para>
/// This is the second inbound adapter onto <see cref="Application.Ports.IConversationPort"/>, and
/// the first one that carries a call. The <c>/v1/chat/completions</c> endpoint is the other. Both
/// read the same contract, so D8 holds and the core never learns a vendor frame schema.
/// </para>
/// <para>
/// The host owns the WebSocket middleware, and its defaults suit a browser rather than a call.
/// Call <c>app.UseWebSockets</c> with a <c>KeepAliveInterval</c> and a <c>KeepAliveTimeout</c> of
/// about 20 seconds. The shipped default is two minutes with no timeout, which lets a dead call
/// hold a session for two minutes.
/// </para>
/// </remarks>
public static class TelnyxRelayEndpointRouteBuilderExtensions
{
    /// <summary>The route this endpoint answers on when the host names none.</summary>
    public const string DefaultPattern = "/v1/telnyx/relay";

    /// <summary>Maps the socket on <see cref="DefaultPattern"/>.</summary>
    /// <param name="endpoints">The route builder of the host.</param>
    /// <returns>The mapped endpoint, so a host adds its own conventions.</returns>
    public static IEndpointConventionBuilder MapTelnyxRelay(this IEndpointRouteBuilder endpoints)
        => endpoints.MapTelnyxRelay(DefaultPattern, new TelnyxRelayOptions());

    /// <summary>Maps the socket on one route.</summary>
    /// <param name="endpoints">The route builder of the host.</param>
    /// <param name="pattern">The route to answer on.</param>
    /// <returns>The mapped endpoint, so a host adds its own conventions.</returns>
    public static IEndpointConventionBuilder MapTelnyxRelay(this IEndpointRouteBuilder endpoints, string pattern)
        => endpoints.MapTelnyxRelay(pattern, new TelnyxRelayOptions());

    /// <summary>Maps the socket on one route, with the limits the host chose.</summary>
    /// <param name="endpoints">The route builder of the host.</param>
    /// <param name="pattern">The route to answer on.</param>
    /// <param name="options">What the endpoint may do, and for how long.</param>
    /// <returns>The mapped endpoint, so a host adds its own conventions.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="TelnyxRelayOptions.MaxFrameBytes"/> is not positive, or
    /// <see cref="TelnyxRelayOptions.IdleTimeout"/> or <see cref="TelnyxRelayOptions.CloseTimeout"/>
    /// is a value <c>Task.Delay</c>, <c>CancelAfter</c>, or <c>Task.WaitAsync</c> would refuse at
    /// run time. Checked here, at startup, rather than left to surface only once a live call
    /// reaches the read loop or the close handshake.
    /// </exception>
    public static IEndpointConventionBuilder MapTelnyxRelay(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        TelnyxRelayOptions options)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrEmpty(pattern);
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);

        // Map, and not MapGet. An HTTP/2 WebSocket arrives as CONNECT rather than GET, and MapGet
        // would answer 405 to it.
        return endpoints.Map(pattern, (HttpContext http) => HandleAsync(http, options));
    }

    /// <summary>The longest delay <c>Task.Delay</c>, <c>CancelAfter</c>, and <c>Task.WaitAsync</c> will all accept.</summary>
    /// <remarks>
    /// One millisecond short of <see cref="uint.MaxValue"/> — about 49.7 days — confirmed on net10
    /// for all three: each throws <see cref="ArgumentOutOfRangeException"/> synchronously for
    /// anything past this, for <see cref="TimeSpan.MaxValue"/>, and for any negative span other
    /// than <see cref="Timeout.InfiniteTimeSpan"/> itself.
    /// </remarks>
    private static readonly TimeSpan MaximumBoundedDelay = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    /// <summary>Rejects a <see cref="TelnyxRelayOptions"/> a live call would fail on, before any call ever reaches it.</summary>
    /// <param name="options">What the endpoint may do, and for how long.</param>
    /// <remarks>
    /// <see cref="TelnyxRelayConnection"/>'s own read loop now orders its idle deadline so an
    /// out-of-range <see cref="TelnyxRelayOptions.IdleTimeout"/> can no longer strand a live
    /// receive against a buffer the pool already reclaimed — that guard holds regardless of what
    /// runs here. This check exists for the host, not for that guard: a value <c>Task.Delay</c>
    /// would refuse should fail the process at startup, with a message naming the option and the
    /// range it needs, not surface for the first time as an unexplained fault on whichever call
    /// happens to need the deadline first.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="options"/> carries a value described in the type's own
    /// <see cref="MapTelnyxRelay(IEndpointRouteBuilder, string, TelnyxRelayOptions)"/> exception doc.
    /// </exception>
    private static void ValidateOptions(TelnyxRelayOptions options)
    {
        // Every frame this endpoint ever reads is measured against this bound before it is
        // written into the message buffer. Zero or negative would refuse the very first byte of
        // every message forever, which is not a limit — it is a call nobody could ever place.
        if (options.MaxFrameBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.MaxFrameBytes,
                "TelnyxRelayOptions.MaxFrameBytes must be positive: it bounds one inbound frame, and "
                + "zero or less would refuse every one of them.");
        }

        ValidateBoundedDelay(nameof(options.IdleTimeout), options.IdleTimeout);
        ValidateBoundedDelay(nameof(options.CloseTimeout), options.CloseTimeout);
    }

    /// <summary>Rejects a <see cref="TimeSpan"/> option outside what a bounded wait built on it will accept.</summary>
    /// <param name="optionName">The property this value came from, for the message.</param>
    /// <param name="value">The value the host set.</param>
    private static void ValidateBoundedDelay(string optionName, TimeSpan value)
    {
        if (value == Timeout.InfiniteTimeSpan)
        {
            return;
        }

        if (value < TimeSpan.Zero || value > MaximumBoundedDelay)
        {
            // optionName, never nameof(value). A host reading ParamName off this exception needs
            // the option it set, and "value" names a local of this method that no caller can see.
            throw new ArgumentOutOfRangeException(
                optionName,
                value,
                $"TelnyxRelayOptions.{optionName} is {value}, which Task.Delay, CancelAfter, and "
                + $"Task.WaitAsync all refuse at run time: it must be Timeout.InfiniteTimeSpan, or "
                + $"between TimeSpan.Zero and {MaximumBoundedDelay} — the longest delay a timer can hold.");
        }
    }

    private static async Task HandleAsync(HttpContext http, TelnyxRelayOptions options)
    {
        if (http.Features.Get<IHttpWebSocketFeature>() is null)
        {
            throw new InvalidOperationException(
                "the relay endpoint needs the WebSocket middleware. Call app.UseWebSockets() before "
                + "app.MapTelnyxRelay().");
        }

        if (!http.WebSockets.IsWebSocketRequest)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var socket = await http.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);

        // The pipeline must stay on the stack for the whole life of the socket. A handler that
        // returns early gets "Cannot write to the response body, the response has completed".
        await TelnyxRelayConnection.RunAsync(http, socket, options).ConfigureAwait(false);
    }
}
