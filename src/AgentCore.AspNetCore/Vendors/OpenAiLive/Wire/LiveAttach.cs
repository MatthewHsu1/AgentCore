namespace AgentCore.AspNetCore.Vendors.OpenAiLive.Wire
{
    /// <summary>What attaching to one accepted call needs.</summary>
    /// <param name="CallId">The call.</param>
    /// <param name="ApiKey">The OpenAI key, sent as a bearer token.</param>
    /// <param name="ApiBase">The API's base address; the attach URL is <see cref="OpenAiLiveWire.AttachUri"/> under it.</param>
    internal sealed record LiveAttach(string CallId, string ApiKey, Uri ApiBase);
}
