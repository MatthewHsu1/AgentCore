namespace AgentCore.Application.Hooks.Gates
{
    /// <summary>How a request reached an AgentCore route.</summary>
    public enum EntryTransport
    {
        /// <summary>An HTTP request.</summary>
        Http,

        /// <summary>A phone call.</summary>
        Call,
    }
}
