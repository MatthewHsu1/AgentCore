namespace AgentCore.Application.Hooks.Notices
{
    /// <summary>A tool approval was asked or answered.</summary>
    /// <param name="Scope">Where and when it happened.</param>
    /// <param name="ToolName">The tool awaiting or holding the approval.</param>
    /// <param name="CallId">The function call id (<c>FunctionCallContent.CallId</c>), for <see cref="ApprovalBy.Hook"/>, <see cref="ApprovalBy.Rule"/> and <see cref="ApprovalBy.Human"/> alike.</param>
    /// <param name="State">Where the approval stands.</param>
    /// <param name="By">Who answered the approval, or who will.</param>
    /// <param name="Reason">The reason given, or <see langword="null"/>.</param>
    public sealed record ApprovalChanged(
        HookScope Scope,
        string ToolName,
        string CallId,
        ApprovalState State,
        ApprovalBy By,
        string? Reason)
        : HookNotice(Scope);
}
