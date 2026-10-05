namespace AgentCore.Application.Hooks
{
    /// <summary>One fact a hook is told about after it happened. AgentCore never waits for a notice.</summary>
    /// <param name="Scope">Where and when it happened.</param>
    public abstract record HookNotice(HookScope Scope)
    {
        /// <summary>Gets the identity of this notice: a version 7 Guid. The audit hook uses it as the row id.</summary>
        public Guid EventId { get; init; }
    }
}
