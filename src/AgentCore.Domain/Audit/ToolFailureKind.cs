namespace AgentCore.Domain.Audit;

/// <summary>
/// The closed set of ways one tool call fails.
/// </summary>
/// <remarks>
/// <para>
/// The set is closed for the reason <see cref="CallEndReason"/> gives. D23 makes the audit table the
/// record of the call and §9 makes it the only long-term record, so a report written years later
/// counts these two facts apart and reads nothing else. Free text cannot be counted.
/// </para>
/// <para>
/// The two facts are genuinely different failures with different owners. A tool that threw is a fault
/// in the tool or in what it reached, and it is read beside the endpoint's own signals.
/// <see cref="Undeclared"/> is a fault in the MODEL: it named a tool the document does not declare, so
/// nothing ran and nothing could have run. Today that fact is recorded nowhere, and it is the one a
/// prompt change fixes.
/// </para>
/// <para>
/// <b>This set is OURS, and it is deliberately narrower than the framework's.</b>
/// <c>FunctionInvokingChatClient.FunctionInvocationStatus</c> is Microsoft's, it is marked as an open
/// set, and it already carries a third member the chain has no fact for. The rule this chain follows
/// is the one <see cref="AuditPayloadKeys.ModerationCategories"/> states in the other direction: a
/// vendor's open set stays open and unchecked, and a set the design owns is closed and checked. A
/// framework status that maps to neither of these two is dropped rather than written, because a
/// vocabulary that grows by one string on every framework release cannot be read a year later.
/// </para>
/// <para>
/// The set is small on purpose, and it stays small. A new kind is a change to this enum, a change to
/// <see cref="ToolFailureKinds"/>, and a new row in the public API file.
/// </para>
/// </remarks>
public enum ToolFailureKind
{
    /// <summary>The model called a tool the document does not declare, so nothing ran.</summary>
    /// <remarks>
    /// The model invented the name. The framework answers it with
    /// <c>Error: Requested function "…" not found.</c> and carries no exception with it, so this
    /// failure spends none of the consecutive-error budget and the turn goes on. That is correct — the
    /// model can recover by calling a tool that exists — but it means nothing else would ever record
    /// that it happened.
    /// </remarks>
    Undeclared = 0,

    /// <summary>A declared tool ran and threw.</summary>
    /// <remarks>
    /// The message of the fault rides beside this under <see cref="AuditPayloadKeys.ToolError"/>. A
    /// tool that ANSWERED with an error result is not this: it returned, the model read the answer,
    /// and nothing failed as far as the framework is concerned. See <c>ToolErrorResult</c>.
    /// </remarks>
    Faulted = 1,
}
