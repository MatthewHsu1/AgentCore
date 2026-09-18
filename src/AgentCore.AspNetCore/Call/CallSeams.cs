using AgentCore.Application.Providers;

namespace AgentCore.AspNetCore.Call;

/// <summary>The names the call seam uses in every failure it raises.</summary>
internal static class CallSeams
{
    /// <summary>The <c>providers.call</c> seam.</summary>
    public static readonly VendorSeam Call =
        new("providers.call", "/providers/call/kind", "options.UseCall(...)");
}
