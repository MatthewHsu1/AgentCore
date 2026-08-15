using System.Runtime.CompilerServices;
using AgentCore.Application.Ports;
using AgentCore.Application.Speech;

namespace AgentCore.AspNetCore.Tests.Fakes;

/// <summary>An input port that replays a scripted stream, refuses a second reader, and counts its disposals.</summary>
/// <param name="script">What the caller does, in order. Empty is a call the caller never spoke on.</param>
internal sealed class FakeSpeechInput(params SpeechInput[] script) : ISpeechInputPort
{
    private int _listening;

    /// <summary>Gets how many times this port was disposed.</summary>
    public int Disposals { get; private set; }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// The refusal is the point of this fake as much as the script is: one socket read twice would
    /// give each reader half the call, and the port promises a second call throws rather than
    /// splitting one call in two.
    /// </para>
    /// <para>
    /// The script running out is this fake's end of the call, and it completes the stream. A
    /// cancelled read throws instead, which is the rule <see cref="ISpeechInputPort.ListenAsync"/>
    /// sets and which the real relay connection obeys the same way — a double that ended quietly
    /// where the real port throws would teach a consumer a <c>catch</c> it does not need, and hide
    /// the one it does.
    /// </para>
    /// </remarks>
    public IAsyncEnumerable<SpeechInput> ListenAsync(CancellationToken cancellationToken = default)
    {
        // Thrown from here, not from the iterator below: an iterator's body does not run until
        // something enumerates it, so a guard inside it would let a second ListenAsync return a
        // stream that only throws later, or never, if nobody enumerates it.
        if (Interlocked.CompareExchange(ref _listening, 1, 0) != 0)
        {
            throw new InvalidOperationException("This port is already being read.");
        }

        return ReplayAsync(cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        Disposals++;
        return ValueTask.CompletedTask;
    }

    /// <summary>Replays the script, one item at a time.</summary>
    /// <param name="cancellationToken">Ends the stream.</param>
    /// <returns>Every scripted item, in order.</returns>
    private async IAsyncEnumerable<SpeechInput> ReplayAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var input in script)
        {
            // Yielded from a continuation rather than inline, so a consumer of this fake meets the
            // same interleaving a real socket gives it and never a stream that completes
            // synchronously inside its own await foreach.
            await Task.Yield();

            // Throws rather than breaking out of the loop: a cancelled read is not the end of a
            // call, and this fake must not be the one place a consumer learns otherwise.
            cancellationToken.ThrowIfCancellationRequested();

            yield return input;
        }
    }
}
