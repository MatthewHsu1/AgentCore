namespace AgentCore.Infrastructure.Llm.OpenAI;

/// <summary>
/// A built-in table of OpenAI model context windows, keyed by model id prefix.
/// </summary>
internal static class OpenAiContextWindows
{
    private static readonly (string Prefix, int Tokens)[] Table =
    [
        ("gpt-6-astra", 1_050_000),
        ("gpt-5.6-sol", 1_050_000),
        ("gpt-5.6-terra", 1_050_000),
        ("gpt-5.6-luna", 1_050_000),
        ("gpt-5.6-cyber", 400_000),
        ("gpt-daybreak-red-latest", 400_000),
        ("gpt-daybreak-blue-latest", 1_050_000),
        ("gpt-5.4-nano", 400_000),
        ("gpt-5.4", 1_050_000),
        ("gpt-4.1-mini", 1_047_576),
        ("gpt-4.1-nano", 1_047_576),
        ("gpt-4.1", 1_047_576),
    ];

    /// <summary>Looks up a model id's context window by its longest matching prefix.</summary>
    /// <param name="modelId">The <c>providers.llm[].model</c> value, such as <c>gpt-4.1-mini-2025-04-14</c>.</param>
    /// <returns>The window in tokens, or <see langword="null"/> when no entry's prefix matches.</returns>
    internal static int? Lookup(string modelId)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelId);

        var best = -1;
        int? tokens = null;
        
        foreach (var (prefix, value) in Table)
        {
            if (prefix.Length > best && modelId.StartsWith(prefix, StringComparison.Ordinal))
            {
                best = prefix.Length;
                tokens = value;
            }
        }

        return tokens;
    }
}
