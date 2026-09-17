using AgentCore.Application.Configuration.Schema;

namespace AgentCore.Application.Configuration.Compilation;

/// <summary>
/// Composes the instructions of one agent from the framework's base, the shared prefix, and the
/// stage delta.
/// </summary>
/// <remarks>
/// <para>
/// Section 8.1 states one caching rule, and it is a cost rule only.
/// <c>agents.defaults.instructions</c> is the stable cached prefix, and each stage appends a delta
/// below it. Anything that changes per turn must sit below the prefix, or it defeats the cache for
/// every later turn. <see cref="Base"/> is the same for every agent in every document, so it sits
/// above the prefix and is part of it.
/// </para>
/// <para>
/// OpenAI caches automatically with no marker, at a 1,024-token minimum prefix, and a cached prompt
/// still counts in full against the rate limit. Breaking the prefix is therefore a billing bug and
/// never a capacity one.
/// </para>
/// </remarks>
public static class AgentInstructions
{
    /// <summary>The separator between the base, the cached prefix, and the stage delta.</summary>
    public const string Separator = "\n\n";

    /// <summary>
    /// The posture every agent starts from: resolve before asking, own what you cannot do, do not
    /// repeat a failed call, and finish. It names no persona, no tone, and no domain, and it yields
    /// to whatever the document says below it. <c>baseInstructions: false</c> drops it.
    /// </summary>
    public static string Base { get; } =
        "Resolve what you can from what you know and what you are given. Ask only for what you "
        + "cannot resolve, and ask for one thing at a time.\n"
        + "If the request needs something you cannot do, say so in one sentence and offer the "
        + "nearest thing you can do.\n"
        + "If a tool fails or returns nothing, change your approach once. Do not repeat the same call.\n"
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
