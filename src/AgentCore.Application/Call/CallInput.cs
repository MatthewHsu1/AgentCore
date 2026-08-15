namespace AgentCore.Application.Call;

/// <summary>
/// One thing the caller did, in the order it happened.
/// </summary>
/// <remarks>
/// <para>
/// One ordered stream carries every inbound event, and the kind is a case of this hierarchy
/// rather than a separate stream for each. Section 3.2 asks for exactly that shape, and
/// <c>RelayFrame</c> in the Telnyx adapter already reads this way.
/// </para>
/// <para>
/// The constructor is <see langword="private protected"/>, so the set is closed: nothing outside
/// this assembly can add a case, and a consumer's switch over the three below is exhaustive.
/// </para>
/// </remarks>
public abstract record CallInput
{
    private protected CallInput()
    {
    }

    /// <summary>The caller spoke.</summary>
    /// <param name="Text">What the recognizer heard.</param>
    /// <param name="Language">The language it reported, for example <c>en</c>.</param>
    /// <param name="IsFinal">
    /// <see langword="true"/> marks the end of the turn. <see langword="false"/> is an interim
    /// transcript, and a recognizer sends many of those for one sentence.
    /// </param>
    public sealed record Utterance(string Text, string Language, bool IsFinal) : CallInput;

    /// <summary>The caller pressed one key.</summary>
    /// <param name="Key">The key. A keypad carries card numbers and PINs, so no log line may hold it.</param>
    public sealed record Keypress(string Key) : CallInput;

    /// <summary>The caller cut the reply off.</summary>
    /// <param name="HeardText">
    /// The text the caller actually heard. An empty string means the caller heard nothing at all,
    /// which is a measured answer and not a missing one: a barge-in can land before the first word
    /// leaves the synthesizer. A consumer must not record an assistant turn for an empty value.
    /// </param>
    /// <param name="PlayedDuration">
    /// How much of the reply played. Measured, never estimated: D28 forbids the estimator that
    /// would otherwise stand here, and item 6c of section 11 asks for the same.
    /// </param>
    public sealed record Barge(string HeardText, TimeSpan PlayedDuration) : CallInput;
}
