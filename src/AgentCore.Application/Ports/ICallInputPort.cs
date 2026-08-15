using AgentCore.Application.Call;

namespace AgentCore.Application.Ports;

/// <summary>
/// What the caller did, for one call, as one ordered stream.
/// </summary>
/// <remarks>
/// <para>
/// One instance serves one call and is disposed with it. That is unlike every other port in this
/// folder, which is bound once for the process, and it is why this one is
/// <see cref="IAsyncDisposable"/>.
/// </para>
/// <para>
/// A barge-in arrives here, on the inbound stream, and not on
/// <see cref="ICallOutputPort"/>. It is an event the caller caused, and the order between "the
/// caller cut in" and "the caller then said this" is what a turn loop must never get wrong. One
/// ordered stream keeps that ordering inside the adapter, which is the only place that has the
/// information to settle it.
/// </para>
/// <para>
/// <b>This port always carries text, whatever the wire carries.</b> A bundled transport such as
/// the Telnyx Conversation Relay receives text frames and passes them straight through. A
/// transport whose socket carries audio runs recognition inside its own adapter and emits text
/// here. Audio stops at the adapter's edge and never enters this assembly, which is D28 and
/// section 11 item 6c. That is also why this port is named for the call and not for speech:
/// <see cref="ISpeechAdapter"/> names the vendor behind each speech role — one vendor, or two —
/// and this does not.
/// </para>
/// </remarks>
public interface ICallInputPort : IAsyncDisposable
{
    /// <summary>Reads what the caller does, until the call ends.</summary>
    /// <param name="cancellationToken">
    /// Abandons the read. Cancelling it throws, and never ends the stream quietly — see the remarks.
    /// </param>
    /// <returns>Every inbound event of the call, in order.</returns>
    /// <remarks>
    /// <para>
    /// <b>One consumer only.</b> This method may not be called a second time on one instance, and
    /// a second call throws. <see cref="IAsyncEnumerable{T}"/> is a factory rather than a stream,
    /// so nothing in the type itself stops two enumerators from being taken over one socket, and
    /// two consumers of one socket would each see half the call.
    /// </para>
    /// <para>
    /// <b>The call ending and the read being cancelled are two different endings, and every
    /// implementation must tell them apart the same way.</b> The call ending — the caller hung up,
    /// the transport went away, the vendor closed the socket — completes the stream normally, and
    /// that is the ordinary end of an <c>await foreach</c> over it. Cancelling
    /// <paramref name="cancellationToken"/> throws <see cref="OperationCanceledException"/> instead,
    /// as any cancelled await does. A consumer therefore reads "the loop ended" as "the call is
    /// over" and needs no flag to ask which happened, and an implementation may not swallow its
    /// consumer's own cancellation into a clean ending: a test double that throws where the real
    /// port completes, or the reverse, is a difference the whole point of this port is that nobody
    /// above it can see.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">This port is already being read.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    IAsyncEnumerable<CallInput> ListenAsync(CancellationToken cancellationToken = default);
}
