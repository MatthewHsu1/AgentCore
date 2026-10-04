using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentCore.Application.Configuration.Schema
{
    /// <summary>
    /// The vendor that carries the conversation, and the limits of the socket it opens.
    /// </summary>
    public sealed record ConversationProviderConfiguration
    {
        /// <summary>Gets the vendor that carries the conversation, such as <c>telnyx-relay</c>.</summary>
        public required string Kind { get; init; }

        /// <summary>Gets how long the socket waits with no inbound frame before ending the conversation, or null for the adapter's default.</summary>
        public int? IdleTimeoutSeconds { get; init; }

        /// <summary>Gets how long teardown gives a stuck task before moving on, or null for the adapter's default.</summary>
        public int? CloseTimeoutSeconds { get; init; }

        /// <summary>Gets the largest inbound frame the socket accepts, in bytes, or null for the adapter's default.</summary>
        public int? MaxFrameBytes { get; init; }

        /// <summary>
        /// Gets how long the call gate (BeforeCall) gives each hook to accept or reject an offered call, in seconds, or null
        /// for 5.
        /// </summary>
        public int? AnswerSeconds { get; init; }

        /// <summary>
        /// Gets the <c>live:</c> block: settings that only the vendor <see cref="Kind"/> names reads and checks. Undefined
        /// when the document writes none.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public JsonElement Live { get; init; }

        /// <summary>
        /// Gets the caller-away prompt a voice adapter opens: <c>{ timeoutSeconds, say }</c>. Voice conversations only.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public JsonElement UserAway { get; init; }

        /// <summary>
        /// Gets the filler each tool id opens while its call is running, keyed by tool id. Voice
        /// conversations only. A tool id this does not name opens no filler.
        /// </summary>
        public IReadOnlyDictionary<string, VoiceFillerConfiguration> Filler { get; init; } =
            ReadOnlyDictionary<string, VoiceFillerConfiguration>.Empty;
    }
}
