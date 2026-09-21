using AgentCore.Application.Runtime.Turn;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Tools.Shell;

namespace AgentCore.Application.Runtime.Harness
{
    /// <summary>
    /// The instructions behind an agent's <c>shell:</c> block: the shell family, version, working
    /// directory and CLI versions, probed once per call through that conversation's executor and rendered
    /// with MAF's own formatter. No <c>StateKeys</c> — the snapshot lives on <see cref="ConversationShells"/>
    /// and dies with the conversation, so resume re-probes a fresh environment.
    /// </summary>
    internal sealed class ConversationShellEnvironmentProvider : AIContextProvider
    {
        private const string NoTurnMessage =
            "A shell: tool runs only while a turn runs through a ConversationSession with a workspace root bound.";

        private readonly ConversationShellOptions _options;

        public ConversationShellEnvironmentProvider(ConversationShellOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            _options = options;
        }

        protected override async ValueTask<AIContext> ProvideAIContextAsync(
            InvokingContext context, CancellationToken cancellationToken = default)
        {
            ConversationShells shells = TurnRegistry.For(context.Session)?.Shells
                ?? throw new InvalidOperationException(NoTurnMessage);

            ShellEnvironmentSnapshot snapshot = await shells.GetEnvironmentAsync(_options, cancellationToken).ConfigureAwait(false);

            return new AIContext
            {
                Instructions = ShellEnvironmentProvider.DefaultInstructionsFormatter(snapshot),
            };
        }
    }
}
