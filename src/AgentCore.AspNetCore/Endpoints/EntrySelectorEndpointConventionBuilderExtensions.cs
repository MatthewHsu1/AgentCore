using Microsoft.AspNetCore.Builder;

namespace AgentCore.AspNetCore.Endpoints
{
    /// <summary>Attaches an <see cref="IEntrySelector"/> to a route AgentCore mapped.</summary>
    public static class EntrySelectorEndpointConventionBuilderExtensions
    {
        /// <summary>Makes the route pick its entry with <typeparamref name="TSelector"/>, in place of the URL.</summary>
        /// <typeparam name="TSelector">The selector. The host registers it; startup fails when it does not.</typeparam>
        /// <param name="builder">The route, as a mapping call returned it.</param>
        /// <returns>The same route, so a host chains its conventions.</returns>
        public static IEndpointConventionBuilder SelectEntry<TSelector>(this IEndpointConventionBuilder builder)
            where TSelector : class, IEntrySelector
        {
            ArgumentNullException.ThrowIfNull(builder);

            return builder.WithMetadata(new EntrySelectorMetadata(typeof(TSelector)));
        }
    }
}
