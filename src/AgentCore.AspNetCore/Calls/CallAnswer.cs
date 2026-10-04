namespace AgentCore.AspNetCore.Calls
{
    /// <summary>What one ask of a call came to.</summary>
    internal enum CallAnswerKind
    {
        /// <summary>The engine answered; the vendor speaks the text.</summary>
        Answered,

        /// <summary>A newer ask replaced this one, or the call ended: nothing is sent.</summary>
        Withdrawn,

        /// <summary>The turn could not run; the vendor speaks the fallback reply.</summary>
        Fallback,
    }

    /// <summary>The answer of one ask.</summary>
    /// <param name="Kind">What the ask came to.</param>
    /// <param name="Text">What the vendor speaks, or empty for <see cref="CallAnswerKind.Withdrawn"/>.</param>
    internal sealed record CallAnswer(CallAnswerKind Kind, string Text)
    {
        internal static CallAnswer Withdrawn { get; } = new(CallAnswerKind.Withdrawn, string.Empty);

        internal static CallAnswer Answered(string text) => new(CallAnswerKind.Answered, text);

        /// <summary>The answer of an ask that could not run: the vendor speaks <paramref name="fallbackReply"/>.</summary>
        internal static CallAnswer FallbackOf(string fallbackReply) => new(CallAnswerKind.Fallback, fallbackReply);
    }
}
