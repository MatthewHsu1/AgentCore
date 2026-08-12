namespace AgentCore.Domain.Audit;

/// <summary>
/// One event, joined to the chain: what it says, what stood before it, and what it hashes to.
/// </summary>
/// <remarks>
/// <para>
/// The link is what a stored row holds. <see cref="AuditEvent"/> alone is not chained, because the
/// same event appended after a different predecessor hashes differently, and that is the whole point
/// of the chain.
/// </para>
/// <para>
/// D23: the <c>CHECK</c> constraint of T56 recomputes <see cref="Hash"/> from
/// <see cref="AuditChainLink.Event"/> and <see cref="PreviousHash"/> inside PostgreSQL, so an
/// attacker who edits a payload and disables the guard trigger still cannot make the row agree with
/// itself.
/// </para>
/// </remarks>
public sealed record AuditChainLink
{
    /// <summary>Gets the event this link carries.</summary>
    public required AuditEvent Event { get; init; }

    /// <summary>
    /// Gets the hash of the link before this one, or <see cref="AuditHash.Genesis"/> for the first
    /// link of the chain.
    /// </summary>
    public required AuditHash PreviousHash { get; init; }

    /// <summary>Gets the hash of this link. It is the identity of the event.</summary>
    public required AuditHash Hash { get; init; }
}
