using Microsoft.Agents.AI;

namespace AgentCore.Application.Skills;

/// <summary>
/// One agent's skills provider, with the script tool removed.
/// </summary>
internal sealed class ReadOnlySkillsProvider : AIContextProvider, IDisposable
{
    private readonly AgentSkillsProvider _inner;

    /// <summary>Creates the wrapper, taking ownership of the provider it wraps.</summary>
    /// <param name="inner">The provider whose tools are filtered.</param>
    /// <exception cref="ArgumentNullException"><paramref name="inner"/> is <see langword="null"/>.</exception>
    internal ReadOnlySkillsProvider(AgentSkillsProvider inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    /// <inheritdoc/>
    protected override async ValueTask<AIContext> InvokingCoreAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        var provided = await _inner.InvokingAsync(context, cancellationToken).ConfigureAwait(false);

        // Null when the agent's filter matched no skill at all.
        provided.Tools = provided.Tools?
            .Where(tool => !string.Equals(tool.Name, AgentSkillsProvider.RunSkillScriptToolName, StringComparison.Ordinal))
            .ToList();

        return provided;
    }

    /// <summary>Disposes the provider this wrapper owns.</summary>
    public void Dispose()
    {
        _inner.Dispose();
        GC.SuppressFinalize(this);
    }
}
