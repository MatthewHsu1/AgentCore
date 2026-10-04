namespace AgentCore.AspNetCore.Vendors.OpenAiLive.Call
{
    /// <summary>
    /// GPT-Live's own instructions: the entry's configured prompt, the fixed paragraph that keeps it from guessing
    /// facts (a plain prompt guessed in 4 of 6 test calls, this paragraph delegated in 6 of 6) and from saying
    /// goodbye on a call only the backend can hang up (a real SIP call: asked to end the call, GPT-Live said goodbye
    /// itself and the line stayed up), and the host's brief, appended exactly as written.
    /// </summary>
    internal static class LiveInstructions
    {
        internal const string NeverGuess =
            "You know no product facts yourself. For any question about products, specifications, orders,"
            + " parts, or policies, delegate to the backend and wait for its answer. Never guess a fact."
            + " You cannot end the call yourself: when the caller wants to end or hang up the call, delegate that"
            + " to the backend, and say goodbye with its answer.";

        internal static string Build(string configured, string? brief)
        {
            ArgumentNullException.ThrowIfNull(configured);

            string instructions = configured.TrimEnd() + "\n\n" + NeverGuess;
            return brief is null ? instructions : instructions + "\n\n" + brief;
        }
    }
}
