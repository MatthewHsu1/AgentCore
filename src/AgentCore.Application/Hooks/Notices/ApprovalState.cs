namespace AgentCore.Application.Hooks.Notices
{
    /// <summary>Where an approval stands.</summary>
    public enum ApprovalState
    {
        /// <summary>The approval was requested.</summary>
        Asked,

        /// <summary>The call was approved.</summary>
        Approved,

        /// <summary>The call was denied.</summary>
        Denied,
    }
}
