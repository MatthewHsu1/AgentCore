using AgentCore.Application.Providers;

namespace AgentCore.AspNetCore.Call;

/// <summary>The names the call seam uses in every failure it raises.</summary>
/// <remarks>
/// <para>
/// One constant, because the guard at startup and the route extension both select the same
/// adapter and must fail with the same pointer when they cannot.
/// </para>
/// <para>
/// It is <see langword="internal"/>: spec §11's Added table does not list it, and D15 makes every
/// public member permanent. Both callers live in this assembly, and an outside implementer of
/// <see cref="ICallTransportAdapter"/> selects nothing —
/// <see cref="CallEndpointRouteBuilderExtensions.MapCall(Microsoft.AspNetCore.Routing.IEndpointRouteBuilder, string)"/>
/// does that for it.
/// </para>
/// </remarks>
internal static class CallSeams
{
    /// <summary>The <c>providers.call</c> seam.</summary>
    public static readonly VendorSeam Call =
        new("providers.call", "/providers/call/kind", "options.UseCall(...)");
}
