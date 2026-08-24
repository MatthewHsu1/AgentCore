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
/// <b>Nothing here decides whether this transport is in use.</b> The composition root reads
/// <c>providers.call</c> while the host starts, picks the one transport it names, and asks it for a
/// handler through <see cref="TelnyxRelayCallAdapter.CreateHandler"/>;
/// <see cref="Call.CallEndpointRouteBuilderExtensions.MapCall(IEndpointRouteBuilder, string)"/>
/// owns only the route string. This type maps whatever it is handed, and it is
/// <see langword="internal"/> so that <see cref="TelnyxRelayCallAdapter"/> is the only thing that
/// hands it anything, apart from the test host, through <c>InternalsVisibleTo</c>: a host names its
/// route once and changes vendors in the document.
/// </para>
/// <para>
/// The host owns the WebSocket middleware, and its defaults suit a browser rather than a call.
/// Call <c>app.UseWebSockets</c> with a <c>KeepAliveInterval</c> and a <c>KeepAliveTimeout</c> of
/// about 20 seconds. The shipped default is two minutes with no timeout, which lets a dead call
/// hold a session for two minutes.
/// </para>
/// </remarks>
internal static class TelnyxRelayEndpointRouteBuilderExtensions
{
    /// <summary>The route the test host maps this endpoint on.</summary>
    /// <remarks>
    /// It is no longer a fallback: <c>MapCall</c> always supplies a pattern, so no production path
    /// reaches this value. It is kept because the relay suite maps its host through
    /// <see cref="MapTelnyxRelay"/> directly and needs one route string both sides agree on.
    /// </remarks>
    public const string DefaultPattern = "/v1/telnyx/relay";

    /// <summary>Maps the socket on one route, with the limits the host chose.</summary>
    /// <param name="endpoints">The route builder of the host.</param>
    /// <param name="pattern">The route to answer on.</param>
    /// <param name="options">What the endpoint may do, and for how long.</param>
    /// <returns>The mapped endpoint, so a host adds its own conventions.</returns>
    /// <remarks>
    /// The options are not checked here. <see cref="TelnyxRelayCallAdapter.BuildOptions"/> is what
    /// builds them out of <c>providers.call</c>, and it refuses a value <c>Task.Delay</c>,
    /// <c>CancelAfter</c>, or <c>Task.WaitAsync</c> would reject before that value is ever written
    /// into a <see cref="TelnyxRelayOptions"/> — with a
    /// <see cref="Application.Configuration.Parsing.ConfigurationLoadException"/> naming the field of
    /// the document rather than a C# property. Checking again here would only repeat that work in
    /// the wrong vocabulary.
    /// </remarks>
    public static IEndpointConventionBuilder MapTelnyxRelay(
        this IEndpointRouteBuilder endpoints,
        string pattern,
        TelnyxRelayOptions options)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrEmpty(pattern);
        ArgumentNullException.ThrowIfNull(options);

        // Map, and not MapGet. An HTTP/2 WebSocket arrives as CONNECT rather than GET, and MapGet
        // would answer 405 to it.
        return endpoints.Map(pattern, (HttpContext http) => HandleAsync(http, options));
    }

    internal static async Task HandleAsync(HttpContext http, TelnyxRelayOptions options)
    {
        if (http.Features.Get<IHttpWebSocketFeature>() is null)
        {
            throw new InvalidOperationException(
                "the relay endpoint needs the WebSocket middleware. Call app.UseWebSockets() before "
                + "app.MapCall().");
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
