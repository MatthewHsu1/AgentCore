using AgentCore.Application.Ports;
using AgentCore.Application.Call;
using AgentCore.AspNetCore.Tests.Fakes;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Call;

/// <summary>
/// The two promises <see cref="ICallInputPort"/> makes that a signature cannot: one consumer for
/// the life of the port, and a cancelled read that throws rather than passing for the end of a call.
/// </summary>
/// <remarks>
/// Pinned on the test double, which is the only implementation reachable without a live socket. The
/// relay connection's own guard is the same three lines over the same <see cref="Interlocked"/>
/// flag, but its constructor is private and <c>RunAsync</c> — the one way in — needs an accepted
/// WebSocket and a request's service provider, so nothing here can hold one. What keeps the two
/// honest is that both quote the same rule from the port's own doc.
/// </remarks>
public sealed class CallInputPortTests
{
    [Fact(Timeout = 30_000)]
    public async Task ASecondListenAsync_ThrowsRatherThanGivingEachReaderHalfTheCall()
    {
        var input = new FakeCallInput(
            new CallInput.Utterance("hello", "en", IsFinal: true),
            new CallInput.Keypress("1"),
            new CallInput.Barge("hel", TimeSpan.FromMilliseconds(240)));

        var heard = new List<CallInput>();
        await foreach (var item in input.ListenAsync(TestContext.Current.CancellationToken))
        {
            heard.Add(item);
        }

        // One ordered stream carries every kind, and the reader takes them in the order they
        // happened rather than one stream per kind.
        Assert.Equal(3, heard.Count);
        Assert.Equal(new CallInput.Utterance("hello", "en", IsFinal: true), heard[0]);
        Assert.Equal(new CallInput.Keypress("1"), heard[1]);
        Assert.Equal(new CallInput.Barge("hel", TimeSpan.FromMilliseconds(240)), heard[2]);

        // Thrown by the call itself, and not by enumerating what it returns: an iterator's body
        // does not run until something reads it, so a guard that waited for the first MoveNext
        // would let a second reader walk away holding a stream that never says no.
        Assert.Throws<InvalidOperationException>(
            () => input.ListenAsync(TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = 30_000)]
    public async Task ACancelledRead_ThrowsRatherThanPassingForTheEndOfTheCall()
    {
        // The difference a consumer cannot see for itself: an await foreach that simply ends means
        // the call is over, so a port that swallowed its own consumer's cancellation would report a
        // call that ended when the call is still up.
        var input = new FakeCallInput(new CallInput.Keypress("1"));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var item in input.ListenAsync(cancellation.Token))
            {
                Assert.Fail($"a cancelled read yielded {item}.");
            }
        });
    }
}
