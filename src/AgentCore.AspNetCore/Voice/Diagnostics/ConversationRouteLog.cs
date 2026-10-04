using Microsoft.Extensions.Logging;

namespace AgentCore.AspNetCore.Voice.Diagnostics
{
    /// <summary>The lines the conversation route writes while the host starts.</summary>
    internal static partial class ConversationRouteLog
    {
        /// <summary>The host registered no conversation adapter, or loaded no document.</summary>
        /// <param name="logger">The logger of the map-time gate, not of any conversation.</param>
        /// <param name="pattern">The route this host asked for and did not get.</param>
        /// <param name="reason">What was missing, in the words a reader can act on.</param>
        [LoggerMessage(
            EventId = 1,
            Level = LogLevel.Information,
            Message = "the conversation route '{Pattern}' is not mapped: {Reason}.")]
        public static partial void RouteNotMapped(ILogger logger, string pattern, string reason);

        /// <summary>The document named a vendor this process dials out to, which owns no route.</summary>
        /// <param name="logger">The logger of the map-time gate, not of any conversation.</param>
        /// <param name="kind">The <c>providers.conversation.kind</c> the document named.</param>
        /// <param name="pattern">The route this host asked for and did not get.</param>
        [LoggerMessage(
            EventId = 2,
            Level = LogLevel.Information,
            Message = "providers.conversation names '{Kind}', which this process dials out to rather than "
                + "being dialled in to, so the conversation route '{Pattern}' is not mapped.")]
        public static partial void DialOutVendorMapsNoRoute(ILogger logger, string kind, string pattern);
    }
}
