namespace AgentCore.AspNetCore.Vendors.OpenAiLive.Call
{
    /// <summary>GPT-Live's instructions: the entry's configured prompt, then the host's brief, appended exactly as written.</summary>
    internal static class LiveInstructions
    {
        internal static string Build(string configured, string? brief)
        {
            ArgumentNullException.ThrowIfNull(configured);

            return brief is null ? configured : configured.TrimEnd() + "\n\n" + brief;
        }
    }
}
