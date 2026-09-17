using AgentCore.Application.Configuration.Schema;

namespace AgentCore.Application.Configuration.Compilation;

/// <summary>
/// Composes the instructions of one agent from the framework's base, the shared prefix, and the
/// stage delta.
/// </summary>
public static class AgentInstructions
{
    /// <summary>The separator between the base, the cached prefix, and the stage delta.</summary>
    public const string Separator = "\n\n";

    /// <summary>
    /// The posture every agent starts from.
    /// </summary>
    public static string Base { get; } =
        "You are a helpful AI assistant that uses tools to complete tasks.\n"
        + "\n"
        + "## General guidelines\n"
        + "\n"
        + "- Think through the task before acting. Break complex work into clear steps.\n"
        + "- Use the tools available to you to gather information, perform actions, and verify results.\n"
        + "- Explain your reasoning and thought process as you work through tasks.\n"
        + "- Explain what you learned and what you are going to do next between tool calls, so the user can follow along with your thought process.\n"
        + "- Avoid making more than 4 tool calls in a row without explaining what you are doing.\n"
        + "- If a tool call fails or returns unexpected results, adapt your approach rather than repeating the same call.\n"
        + "- When you have completed the task, present a clear and concise summary of what you did and what you found.\n"
        + "\n"
        + "Resolve what you can from what you know and what you are given. Ask only for what you "
        + "cannot resolve, and ask for one thing at a time.\n"
        + "If the request needs something you cannot do, say so in one sentence and offer the "
        + "nearest thing you can do.\n"
        + "Never invent a fact, a value, or a source.\n"
        + "Finish the task. Do not stop at the easy part.\n"
        + "The instructions below take priority over these.";

    /// <summary>Puts the base and the shared prefix above the delta of one agent.</summary>
    /// <param name="defaults">The <c>agents.defaults</c> section, or <see langword="null"/>.</param>
    /// <param name="agent">The agent whose delta goes below the prefix.</param>
    /// <returns>The composed instructions, or <see langword="null"/> when no part exists.</returns>
    public static string? Compose(AgentDefaults? defaults, AgentConfiguration agent)
    {
        ArgumentNullException.ThrowIfNull(agent);

        var parts = new List<string>(3);

        if (AgentHarness.Compose(defaults, agent).BaseInstructions)
        {
            parts.Add(Base);
        }

        // The prefix always comes before the delta. Nothing per-turn inside either.
        if (Trim(defaults?.Instructions) is { } prefix)
        {
            parts.Add(prefix);
        }

        if (Trim(agent.Instructions) is { } delta)
        {
            parts.Add(delta);
        }

        return parts.Count == 0 ? null : string.Join(Separator, parts);
    }

    private static string? Trim(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var trimmed = text.Trim('\n', '\r');
        return trimmed.Length == 0 ? null : trimmed;
    }
}
