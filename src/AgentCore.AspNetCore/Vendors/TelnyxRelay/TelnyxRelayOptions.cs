using AgentCore.AspNetCore.Voice;

namespace AgentCore.AspNetCore.Vendors.TelnyxRelay
{
    /// <summary>
    /// What the relay endpoint may do, and for how long.
    /// </summary>
    internal sealed class TelnyxRelayOptions
    {
        /// <summary>Gets or sets the largest inbound frame the endpoint accepts, in bytes.</summary>
        public int MaxFrameBytes { get; set; } = 64 * 1024;

        /// <summary>Gets or sets how long teardown gives a stuck task before it moves on.</summary>
        public TimeSpan CloseTimeout { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>Gets or sets how long the endpoint waits with no inbound frame before it ends the conversation.</summary>
        public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(5);

        /// <summary>Gets or sets the away prompt and the per-tool filler this conversation's voice loop runs.</summary>
        public VoiceOptions Voice { get; set; } = VoiceOptions.Default;
    }
}
