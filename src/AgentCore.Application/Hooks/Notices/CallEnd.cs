namespace AgentCore.Application.Hooks.Notices
{
    /// <summary>The call part of a conversation that ended on the phone.</summary>
    /// <param name="CallId">The transport's own id of the call.</param>
    /// <param name="Seconds">How long the call lasted, in seconds.</param>
    /// <param name="Cause">The transport's word for why the call ended, or <see langword="null"/>.</param>
    public sealed record CallEnd(string CallId, double Seconds, string? Cause);
}
