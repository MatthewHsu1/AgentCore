namespace AgentCore.Application.Hooks
{
    /// <summary>What a gate does when a hook throws or misses its deadline.</summary>
    public enum HookFailure
    {
        /// <summary>Act as if the hook did nothing: its staged verbs are dropped and the chain goes on.</summary>
        Open,

        /// <summary>Apply the gate's safe verb and stop the chain.</summary>
        Closed,
    }
}
