using AgentCore.AspNetCore.Vendors.TelnyxRelay.Wire;
namespace AgentCore.AspNetCore.Vendors.TelnyxRelay.Connection
{
    /// <summary>One item queued for the write loop, carrying the reply generation it was written under.</summary>
    /// <param name="Generation">
    /// The reply generation <see cref="Frame"/> was queued under, or <see langword="null"/> for a
    /// frame no reply owns — nothing writes one of those yet, but the write loop's gate only applies
    /// when a generation is present, so a future frame outside the reply lifecycle (a close handoff,
    /// for one) can opt out by carrying none. This type never reaches <see cref="TelnyxRelayJson"/>:
    /// only <see cref="Frame"/> is serialized, so nothing here changes the wire.
    /// </param>
    /// <param name="Frame">The frame to serialize and send: a <see cref="RelayToken"/> today.</param>
    internal readonly record struct OutboundItem(long? Generation, object Frame);
}
