using AgentCore.AspNetCore.Vendors.OpenAiLive.Wire;
namespace AgentCore.AspNetCore.Vendors.OpenAiLive.Webhook
{
    /// <summary>How OpenAI answered an accept.</summary>
    internal enum AcceptOutcome
    {
        Accepted,

        /// <summary>Another accept or reject of the call came first (<see cref="OpenAiLiveWire.DecisionAlreadyMade"/>).</summary>
        AlreadyDecided,

        Failed,
    }
}
