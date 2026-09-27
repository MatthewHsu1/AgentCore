using AgentCore.Application.Configuration.Schema;
using Microsoft.AspNetCore.Http;

namespace AgentCore.AspNetCore.Voice
{
    /// <summary>
    /// A conversation vendor this process is dialled <b>in</b> to, which therefore owns an inbound route.
    /// </summary>
    public interface IConversationTransportAdapter : IConversationAdapter
    {
        /// <summary>Builds the handler the conversation route answers every entry with.</summary>
        /// <param name="configuration">The <c>providers.conversation</c> block, including its limits.</param>
        /// <returns>
        /// The delegate the conversation route runs. The URL names the entry, and the route has already
        /// refused one the document does not declare; the handler reads it with
        /// <see cref="ConversationEndpointRouteBuilderExtensions.EntryOf"/>.
        /// </returns>
        RequestDelegate CreateHandler(ConversationProviderConfiguration configuration);
    }
}
