using Microsoft.AspNetCore.Builder;

namespace AgentCore.Hosting
{
    /// <summary>The routes <see cref="AgentCoreHostEndpointExtensions.MapAgentCoreHost"/> mapped, so a host adds its own conventions to each.</summary>
    /// <param name="Health">The liveness check.</param>
    /// <param name="Responses">The OpenAI-compatible Responses endpoint.</param>
    /// <param name="Call">The conversation socket.</param>
    public sealed record AgentCoreEndpoints(
        IEndpointConventionBuilder Health,
        IEndpointConventionBuilder Responses,
        IEndpointConventionBuilder Call);
}
