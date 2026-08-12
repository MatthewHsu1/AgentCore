using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Secrets;

namespace AgentCore.AspNetCore.DependencyInjection;

/// <summary>
/// What <c>AddAgentCore</c> already knows when it asks the host to build one seam.
/// </summary>
/// <remarks>
/// The document loads and the secrets resolve before any adapter is built, so every adapter the host
/// contributes reads both. That is why the knowledge store can read <c>providers.knowledge</c> and
/// the HTTP tool factory can read the resolved secrets without either of them loading anything.
/// </remarks>
public sealed class AgentCoreStartup
{
    /// <summary>Creates the context.</summary>
    /// <param name="configuration">The loaded and validated document.</param>
    /// <param name="secrets">Every <c>${secret:name}</c> value the document needs.</param>
    internal AgentCoreStartup(AgentCoreConfiguration configuration, ResolvedSecrets secrets)
    {
        Configuration = configuration;
        Secrets = secrets;
    }

    /// <summary>Gets the loaded document. It already passed checks 1 to 8.</summary>
    public AgentCoreConfiguration Configuration { get; }

    /// <summary>Gets the resolved secrets. It reads out values through its own formatter only.</summary>
    public ResolvedSecrets Secrets { get; }
}
