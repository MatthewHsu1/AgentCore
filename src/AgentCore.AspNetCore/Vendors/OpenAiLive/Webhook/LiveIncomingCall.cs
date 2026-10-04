using AgentCore.AspNetCore.Vendors.OpenAiLive.Wire;
namespace AgentCore.AspNetCore.Vendors.OpenAiLive.Webhook
{
    /// <summary>One incoming-call webhook, as <see cref="OpenAiLiveWire"/> reads it.</summary>
    /// <param name="CallId">The id every call URL names.</param>
    /// <param name="From">The caller's number from the SIP <c>From</c> header, or <see langword="null"/>.</param>
    /// <param name="To">The called number from the SIP <c>To</c> header, or <see langword="null"/>.</param>
    /// <param name="Headers">Every SIP header, by name, case-insensitive; repeated names joined with ", ".</param>
    internal sealed record LiveIncomingCall(string CallId, string? From, string? To, IReadOnlyDictionary<string, string> Headers);
}
