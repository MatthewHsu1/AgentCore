using AgentCore.Application.Calls.Memory;
using AgentCore.Application.Transcript;
using Microsoft.Extensions.AI;

namespace AgentCore.TestSupport;

/// <summary>
/// A store that holds its first write of words open until a test releases it.
/// </summary>
/// <remarks>
/// A real store talks to a database, so a write outlives the turn that queued it. The memory store
/// every other test runs on lands a write before the next line reads it, and would let teardown drop
/// a session with a write still owing without any test noticing.
/// </remarks>
public sealed class ParkingCallStore() : DelegatingCallStore(new InMemoryCallStore())
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _landed;

    /// <summary>Gets a task that completes once the first append is parked.</summary>
    public Task Parked => _entered.Task;

    /// <summary>Gets whether the parked append has finished writing.</summary>
    public bool Landed => _landed;

    /// <summary>Lets the parked append finish.</summary>
    public void Release() => _release.TrySetResult();

    /// <inheritdoc />
    public override async ValueTask AppendAsync(
        IReadOnlyList<CallMessage> messages, CancellationToken cancellationToken = default)
    {
        _entered.TrySetResult();
        await _release.Task.WaitAsync(cancellationToken);
        _landed = true;
    }



}