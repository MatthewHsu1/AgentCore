using AgentCore.Application.Hooks.Notices;

namespace AgentCore.AspNetCore.Vendors.OpenAiLive.Wire
{
    /// <summary>One sideband event this adapter acts on.</summary>
    internal abstract record LiveEvent
    {
        private protected LiveEvent()
        {
        }

        /// <summary>A piece of what one side said. Times are milliseconds from the session's start; <c>start_ms</c> of the last word, not its end.</summary>
        internal sealed record Transcript(Speaker Speaker, string Delta, int StartMs, int EndMs) : LiveEvent;

        /// <summary>GPT-Live asks the backend for help. It carries no task text.</summary>
        internal sealed record Delegation(string Id, int? OffsetMs) : LiveEvent;

        /// <summary>
        /// GPT-Live took one <c>session.commentary.append</c>. <paramref name="EventId"/> is the <c>event_id</c> that
        /// append carried, which the ack echoes as <c>client_event_id</c>.
        /// </summary>
        internal sealed record Appended(string? EventId) : LiveEvent;

        /// <summary>The session ended.</summary>
        internal sealed record Closed(string? Reason) : LiveEvent;

        /// <summary>GPT-Live reported an error.</summary>
        internal sealed record Failed(string? Code, string? Message) : LiveEvent;

        /// <summary>An event this adapter does not act on, or a known one with a field missing.</summary>
        internal sealed record Other(string Type) : LiveEvent;
    }
}
