using AgentCore.Application.Configuration.Schema;

namespace AgentCore.Application.Configuration.Compilation;

/// <summary>
/// Composes the instructions of one agent from the shared prefix and the stage delta.
/// </summary>
/// <remarks>
/// <para>
/// Section 8.1 states one caching rule, and it is a cost rule only.
/// <c>agents.defaults.instructions</c> is the stable cached prefix, and each stage appends a delta
/// below it. Anything that changes per turn must sit below the prefix, or it defeats the cache for
/// every later turn.
/// </para>
/// <para>
/// OpenAI caches automatically with no marker, at a 1,024-token minimum prefix, and a cached prompt
/// still counts in full against the rate limit. Breaking the prefix is therefore a billing bug and
/// never a capacity one.
/// </para>
/// </remarks>
public static class AgentInstructions
{
    /// <summary>The separator between the cached prefix and the stage delta.</summary>
    public const string Separator = "\n\n";

    /// <summary>Puts the shared prefix above the delta of one agent.</summary>
    /// <param name="defaults">The <c>agents.defaults</c> section, or <see langword="null"/>.</param>
    /// <param name="agent">The agent whose delta goes below the prefix.</param>
    /// <returns>The composed instructions, or <see langword="null"/> when neither part exists.</returns>
    public static string? Compose(AgentDefaults? defaults, AgentConfiguration agent)
    {
        ArgumentNullException.ThrowIfNull(agent);

        var prefix = Trim(defaults?.Instructions);
        var delta = Trim(agent.Instructions);

        if (prefix is null)
        {
            return delta;
        }

        // The prefix always comes first. Nothing above it, and nothing per-turn inside it.
        return delta is null ? prefix : prefix + Separator + delta;
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
