namespace AgentCore.Application.Hooks.Engine
{
    /// <summary>Scopes for the gates and notices that have no session.</summary>
    internal static class HookScopes
    {
        internal static HookScope Host(DateTimeOffset now)
        {
            return new(null, null, null, null, Guid.Empty, 0, now);
        }

        internal static HookScope ForGate(string? conversationId, string? entry, DateTimeOffset now)
        {
            return new(conversationId, entry, null, null, Guid.Empty, 0, now);
        }
    }
}
