using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Skills
{
    /// <summary>
    /// One agent's skills provider, with the script tool removed and <c>load_skill</c> pointing a
    /// pinned name back at the instructions.
    /// </summary>
    internal sealed class ReadOnlySkillsProvider : AIContextProvider, IDisposable
    {
        private readonly AgentSkillsProvider _inner;

        private readonly IReadOnlySet<string> _pinned;

        /// <summary>Creates the wrapper, taking ownership of the provider it wraps.</summary>
        /// <param name="inner">The provider whose tools are filtered.</param>
        /// <param name="pinned">The names this agent pins, which <c>load_skill</c> answers without loading.</param>
        /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
        internal ReadOnlySkillsProvider(AgentSkillsProvider inner, IReadOnlySet<string> pinned)
        {
            ArgumentNullException.ThrowIfNull(inner);
            ArgumentNullException.ThrowIfNull(pinned);
            _inner = inner;
            _pinned = pinned;
        }

        /// <inheritdoc/>
        protected override async ValueTask<AIContext> InvokingCoreAsync(
            InvokingContext context,
            CancellationToken cancellationToken = default)
        {
            AIContext provided = await _inner.InvokingAsync(context, cancellationToken).ConfigureAwait(false);

            // Null when the agent's filter matched no skill at all.
            provided.Tools = provided.Tools?
                .Where(tool => !string.Equals(tool.Name, AgentSkillsProvider.RunSkillScriptToolName, StringComparison.Ordinal))
                .Select(RedirectPinned)
                .ToList();

            return provided;
        }

        /// <summary>Disposes the provider this wrapper owns.</summary>
        public void Dispose()
        {
            _inner.Dispose();
            GC.SuppressFinalize(this);
        }

        private AITool RedirectPinned(AITool tool)
        {
            return _pinned.Count > 0
                && tool is AIFunction function
                && string.Equals(function.Name, AgentSkillsProvider.LoadSkillToolName, StringComparison.Ordinal)
                ? new PinnedSkillRedirect(function, _pinned)
                : tool;
        }
    }
}
