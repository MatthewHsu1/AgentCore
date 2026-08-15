using AgentCore.Application.Call;

namespace AgentCore.Application.Ports;

/// <summary>
/// What the agent says, for one call.
/// </summary>
/// <remarks>
/// <para>
/// One instance serves one call and is disposed with it, as <see cref="ICallInputPort"/> is.
/// </para>
/// <para>
/// Three methods and no events. Every studied voice framework gives its speaking side a method
/// and never a callback the core subscribes to, and the rule is worth more in C# than in the
/// languages they are written in: an event promises no ordering across threads.
/// </para>
/// </remarks>
public interface ICallOutputPort : IAsyncDisposable
{
    /// <summary>Speaks one fragment of the reply.</summary>
    /// <param name="fragment">The text to speak. Never empty.</param>
    /// <param name="cancellationToken">Stops waiting for room to send.</param>
    /// <remarks>
    /// One fragment, never a buffered sentence. A synthesizer that paces its own audio starts
    /// speaking before the model finishes the reply, and buffering to a sentence would add the
    /// whole first sentence to the time before the caller hears anything.
    /// </remarks>
    ValueTask SpeakAsync(string fragment, CancellationToken cancellationToken = default);

    /// <summary>Closes one reply, so the synthesizer knows no more is coming.</summary>
    /// <param name="cancellationToken">Stops waiting for room to send.</param>
    ValueTask CompleteAsync(CancellationToken cancellationToken = default);

    /// <summary>Starts stopping the reply, and drops whatever has not been spoken.</summary>
    /// <param name="cancellationToken">Stops waiting on the transport.</param>
    /// <remarks>
    /// <b>This does not return the cut point, and the two are not simultaneous.</b> The settled
    /// answer — what the caller heard, and for how long — arrives afterwards as a
    /// <see cref="CallInput.Barge"/> on the input stream. A vendor that reports the cut itself
    /// settles at once. One built from a separate synthesizer must wait for its own audio to
    /// finish draining first, and every implementation of that wait carries a deadline: two
    /// seconds is the budget, after which the reply counts as fully heard.
    /// </remarks>
    ValueTask StopAsync(CancellationToken cancellationToken = default);
}
