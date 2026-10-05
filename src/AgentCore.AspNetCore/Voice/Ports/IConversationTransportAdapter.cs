using AgentCore.Application.Configuration.Schema;
using AgentCore.AspNetCore.Voice.Routing;

namespace AgentCore.AspNetCore.Voice.Ports
{
    /// <summary>
    /// A conversation vendor this process is dialled <b>in</b> to, which therefore owns an inbound route.
    /// </summary>
    public interface IConversationTransportAdapter : IConversationAdapter
    {
        /// <summary>Builds the route every entry answers on, with the check that the caller is the vendor.</summary>
        /// <param name="configuration">The <c>providers.conversation</c> block, including its limits.</param>
        /// <returns>
        /// The route. Its handler runs once the caller passed the check and the route picked the entry and refused one
        /// the document does not declare; the handler reads it with
        /// <see cref="ConversationEndpointRouteBuilderExtensions.EntryOf"/>.
        /// </returns>
        ConversationRoute CreateRoute(ConversationProviderConfiguration configuration);
    }
}
