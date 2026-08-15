using AgentCore.Application.Ports;

namespace AgentCore.Application.Speech;

/// <summary>
/// The two halves of one call's speech, opened together and disposed together.
/// </summary>
/// <remarks>
/// <para>
/// A vendor that does both halves itself returns one object in both slots. A pair of separate
/// services returns two. Nothing above this type may ask which it got, and nothing above it can
/// tell: one, two, or two sharing private state all read the same here.
/// </para>
/// <para>
/// The bundled shape is why both ports speak text. Conversation Relay runs the audio on its own side of the
/// wire: transcripts arrive as text, replies leave as text, and the barge-in answer — what the
/// caller heard, and for how long — arrives already measured, in the vendor's own frame. One
/// connection is honestly both halves, so the factory hands the same object to both slots.
/// </para>
/// <code>
/// caller ─audio─▶ [ conversation relay ] ─text─▶ RelayConnection ─▶ agent
/// caller ◀─audio─ [ conversation relay ] ◀─text─ RelayConnection ◀─ agent reply
/// </code>
/// <para>
/// Two objects at the interface does not mean two independent objects underneath. A split adapter
/// measures the played duration on its speaking side and reports it on its listening side, so its
/// two halves share one clock. That sharing belongs to whichever adapter created them, which is
/// why it appears on neither interface.
/// </para>
/// <para>
/// That clock is the layer of indirection a split adapter must add. Separate STT and TTS services
/// merge nothing, so the adapter wraps the two vendor clients — the
/// <c>Microsoft.Extensions.AI</c> pair, <c>ISpeechToTextClient</c> and <c>ITextToSpeechClient</c>,
/// which stay inside the adapter and never enter this assembly, because they sit at the audio
/// boundary and are marked experimental — and computes for itself what the bundled vendor
/// reported for free: bytes written minus the transport's own queue, counted from the first
/// audibly played sound, mapped back to the words the model wrote — measured, never estimated.
/// The settled answer still arrives as a <see cref="SpeechInput.Barge"/> on the input stream, and
/// every settle path after <see cref="ISpeechOutputPort.StopAsync"/> carries a two-second
/// deadline, after which the reply counts as fully heard. Sections 2, 6, and 11 of the design
/// record the argument.
/// </para>
/// <code>
/// caller ─audio─▶ [ STT client ] ─text─▶ split input port  ─┐
///                                                            ├─ SpeechChannel ─▶ agent
/// caller ◀─audio─ [ TTS client ] ◀─text─ split output port ─┘
///                        │
///                 [ playback clock ]
/// </code>
/// </remarks>
/// <param name="Input">What the caller does.</param>
/// <param name="Output">What the agent says.</param>
public sealed record SpeechChannel(ISpeechInputPort Input, ISpeechOutputPort Output) : IAsyncDisposable
{
    private bool _disposed;

    /// <summary>Disposes both halves, and each underlying object exactly once.</summary>
    /// <remarks>
    /// Reference equality is the test, because one object filling both slots is the ordinary case
    /// and not the exception. Disposing it twice would let an adapter's own teardown run twice.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await Input.DisposeAsync().ConfigureAwait(false);

        if (!ReferenceEquals(Input, Output))
        {
            await Output.DisposeAsync().ConfigureAwait(false);
        }
    }
}
