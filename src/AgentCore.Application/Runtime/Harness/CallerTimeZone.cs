using Microsoft.Agents.AI;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.Application.Runtime.Harness
{
    /// <summary>
    /// The time zone the person on a conversation is in, as the host learned it — from the browser, on the
    /// request. The clock line reads the date in it instead of the server's zone. It is set per
    /// request, not once: a person who travels moves the clock with them, and a request that names
    /// no zone leaves the last one in place.
    /// </summary>
    public static class CallerTimeZone
    {
        /// <summary>The state key a background child's session carries the zone under. The value is a zone id.</summary>
        internal const string Key = "urn:agentcore:time-zone";

        /// <summary>Reads a zone id the way the host received it, or nothing when it names no zone.</summary>
        /// <param name="id">An IANA id such as <c>America/Chicago</c>, or a Windows id.</param>
        /// <returns>The zone, or <see langword="null"/> for an empty or unknown id.</returns>
        public static TimeZoneInfo? Parse(string? id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            return TimeZoneInfo.TryFindSystemTimeZoneById(id.Trim(), out TimeZoneInfo? zone) ? zone : null;
        }

        /// <summary>Puts the caller's zone on the conversation a session carries, replacing what was there.</summary>
        /// <param name="session">A session this framework created: it names the conversation.</param>
        /// <param name="zone">The zone the caller is in.</param>
        /// <exception cref="InvalidOperationException">The session names no conversation.</exception>
        public static void Set(AgentSession session, TimeZoneInfo zone)
        {
            ArgumentNullException.ThrowIfNull(session);
            ArgumentNullException.ThrowIfNull(zone);

            if (session.GetService<ConversationSession>() is not { } conversation)
            {
                throw new InvalidOperationException(
                    "The session is not one this agent created, so it names no call to put the zone on.");
            }

            conversation.Runner.SetTimeZone(zone);
        }

        /// <summary>Stamps the zone onto a child session, so a run on it reads the caller's date.</summary>
        /// <param name="session">The child's session.</param>
        /// <param name="zone">The zone the caller is in.</param>
        internal static void Stamp(AgentSession session, TimeZoneInfo zone)
        {
            session.StateBag.SetValue(Key, zone.Id);
        }

        /// <summary>Reads the zone stamped on a child session, or nothing when none was.</summary>
        /// <param name="session">The session, or <see langword="null"/>.</param>
        internal static TimeZoneInfo? Stamped(AgentSession? session)
        {
            return session is not null && session.StateBag.TryGetValue(Key, out string? id) ? Parse(id) : null;
        }
    }
}
