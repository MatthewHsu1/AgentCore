
using AgentCore.AspNetCore.Voice.Ports;

namespace AgentCore.AspNetCore.Vendors.TelnyxRelay
{
    /// <summary>
    /// The selection face of the one speech vendor this solution bundles.
    /// </summary>
    public sealed class TelnyxRelaySpeechAdapter : ISpeechAdapter
    {
        /// <summary>The one <c>kind</c> value, under either speech role, this vendor answers to.</summary>
        public const string TelnyxRelayKind = "telnyx-relay";

        /// <inheritdoc/>
        public string Kind => TelnyxRelayKind;
    }
}
