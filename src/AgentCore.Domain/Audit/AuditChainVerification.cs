namespace AgentCore.Domain.Audit;

/// <summary>
/// What <see cref="AuditChain.Verify"/> found: the chain is whole, or the first link that is not.
/// </summary>
/// <remarks>
/// Section 11, item 6, asks <c>chain_check</c> to return <c>ok</c>. This record is that answer, and
/// it names the FIRST broken link rather than counting them. A chain breaks once and stays broken,
/// because every later hash is computed over the broken one, so a count of broken links measures the
/// length of the tail and not the size of the damage.
/// </remarks>
public sealed record AuditChainVerification
{
    private AuditChainVerification(string result, int brokenLinkIndex, string? reason)
    {
        Result = result;
        BrokenLinkIndex = brokenLinkIndex;
        Reason = reason;
    }

    /// <summary>The <see cref="Result"/> of a whole chain.</summary>
    public const string OkResult = "ok";

    /// <summary>The <see cref="Result"/> of a chain whose hashes stopped agreeing.</summary>
    public const string LinkBrokenResult = "link-broken";

    /// <summary>Gets <see cref="OkResult"/> or <see cref="LinkBrokenResult"/>.</summary>
    public string Result { get; }

    /// <summary>Gets whether every link agrees with the one before it.</summary>
    public bool IsIntact => Result == OkResult;

    /// <summary>
    /// Gets the zero-based index of the first link that does not agree, or <c>-1</c> when the chain
    /// is whole.
    /// </summary>
    public int BrokenLinkIndex { get; }

    /// <summary>
    /// Gets what is wrong with that link, or <see langword="null"/> when the chain is whole.
    /// </summary>
    public string? Reason { get; }

    /// <summary>Reports a whole chain.</summary>
    /// <returns>The verification.</returns>
    public static AuditChainVerification Ok() => new(OkResult, -1, null);

    /// <summary>Reports the first link that does not agree.</summary>
    /// <param name="brokenLinkIndex">The zero-based index of that link.</param>
    /// <param name="reason">What is wrong with it.</param>
    /// <returns>The verification.</returns>
    public static AuditChainVerification LinkBroken(int brokenLinkIndex, string reason) =>
        new(LinkBrokenResult, brokenLinkIndex, reason);

    /// <summary>Reads the result and, when the chain is broken, the link and the reason.</summary>
    /// <returns>The text.</returns>
    public override string ToString() =>
        IsIntact ? OkResult : $"{LinkBrokenResult} at link {BrokenLinkIndex}: {Reason}";
}
