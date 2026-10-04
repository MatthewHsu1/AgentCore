namespace AgentCore.Application.Hooks.Notices
{
    /// <summary>Who answered an approval, or who will.</summary>
    public enum ApprovalBy
    {
        /// <summary>A hook.</summary>
        Hook,

        /// <summary>A person.</summary>
        Human,

        /// <summary>A rule of the entry.</summary>
        Rule,
    }
}
