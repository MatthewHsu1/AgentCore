namespace AgentCore.Application.Hooks.Gates
{
    /// <summary>Why a call was turned away. Each transport maps these to its own code.</summary>
    public enum CallRefusal
    {
        /// <summary>A hook declined the call. What declining means is up to the hook and the host.</summary>
        Declined,

        /// <summary>A hook turned the call away as busy. What busy means is up to the hook and the host.</summary>
        Busy,

        /// <summary>
        /// No hook accepted the call: none answered, a hook failed, or a hook missed the gate's deadline. A hook may
        /// also give it.
        /// </summary>
        Unavailable,
    }
}
