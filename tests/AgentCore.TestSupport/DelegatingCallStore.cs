using System.Text.Json;
using AgentCore.Application.Calls;
using AgentCore.Application.Ports;
using AgentCore.Application.Transcript;
using Microsoft.Extensions.AI;

namespace AgentCore.TestSupport;

/// <summary>
/// A store that forwards every call to another one, so a fake overrides only what it is testing.
/// </summary>
/// <remarks>
/// One store holds a call and its words, which is fifteen members. A fake that cares about one of
/// them should not carry fourteen stubs to say so.
/// </remarks>
/// <param name="inner">Where an un-overridden call goes.</param>
public abstract class DelegatingCallStore(ICallStore inner) : ICallStore
{
    /// <summary>Gets the store behind this one.</summary>
    protected ICallStore Inner { get; } = inner;

    /// <inheritdoc />
    public virtual ValueTask<CallRecord> CreateAsync(
        string callId, CancellationToken cancellationToken = default)
        => Inner.CreateAsync(callId, cancellationToken);

    /// <inheritdoc />
    public virtual ValueTask<CallRecord?> GetAsync(
        string callId, CancellationToken cancellationToken = default)
        => Inner.GetAsync(callId, cancellationToken);

    /// <inheritdoc />
    public virtual ValueTask<CallPage> ListAsync(
        string principalKey,
        string? after,
        int limit,
        CallStatus? status = null,
        CancellationToken cancellationToken = default)
        => Inner.ListAsync(principalKey, after, limit, status, cancellationToken);

    /// <inheritdoc />
    public virtual ValueTask RenameAsync(
        string callId, string title, CancellationToken cancellationToken = default)
        => Inner.RenameAsync(callId, title, cancellationToken);

    /// <inheritdoc />
    public virtual ValueTask SetStatusAsync(
        string callId, CallStatus status, CancellationToken cancellationToken = default)
        => Inner.SetStatusAsync(callId, status, cancellationToken);

    /// <inheritdoc />
    public virtual ValueTask SetCustomAsync(
        string callId, JsonElement? custom, CancellationToken cancellationToken = default)
        => Inner.SetCustomAsync(callId, custom, cancellationToken);

    /// <inheritdoc />
    public virtual ValueTask SetExternalIdAsync(
        string callId, string? externalId, CancellationToken cancellationToken = default)
        => Inner.SetExternalIdAsync(callId, externalId, cancellationToken);

    /// <inheritdoc />
    public virtual ValueTask DeleteAsync(string callId, CancellationToken cancellationToken = default)
        => Inner.DeleteAsync(callId, cancellationToken);

    /// <inheritdoc />
    public virtual ValueTask AppendAsync(
        IReadOnlyList<CallMessage> messages,
        CallSessionState? state = null,
        CancellationToken cancellationToken = default)
        => Inner.AppendAsync(messages, state, cancellationToken);

    /// <inheritdoc />
    public virtual ValueTask RewriteAsync(
        string callId, int ordinal, ChatMessage content, CancellationToken cancellationToken = default)
        => Inner.RewriteAsync(callId, ordinal, content, cancellationToken);

    /// <inheritdoc />
    public virtual ValueTask<IReadOnlyList<CallMessage>> ReadAsync(
        string callId, CancellationToken cancellationToken = default)
        => Inner.ReadAsync(callId, cancellationToken);

    /// <inheritdoc />
    public virtual ValueTask<int> EraseAsync(
        string callId, CancellationToken cancellationToken = default)
        => Inner.EraseAsync(callId, cancellationToken);

    /// <inheritdoc />
    public virtual ValueTask<int> SweepAsync(
        TimeSpan retention, int batchSize = 500, CancellationToken cancellationToken = default)
        => Inner.SweepAsync(retention, batchSize, cancellationToken);

    /// <inheritdoc />
    public virtual ValueTask AttachPrincipalAsync(
        string callId, string principalKey, string role, CancellationToken cancellationToken = default)
        => Inner.AttachPrincipalAsync(callId, principalKey, role, cancellationToken);

    /// <inheritdoc />
    public virtual ValueTask DetachPrincipalAsync(
        string callId, string principalKey, CancellationToken cancellationToken = default)
        => Inner.DetachPrincipalAsync(callId, principalKey, cancellationToken);
}
