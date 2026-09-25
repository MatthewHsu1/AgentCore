using AgentCore.Application.Runtime.Turn;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Tools.Shell;

namespace AgentCore.Application.Runtime.Harness
{
    /// <summary>
    /// The instructions behind an agent's <c>shell:</c> block.
    /// </summary>
    internal sealed class ConversationShellEnvironmentProvider : AIContextProvider
    {
        private readonly ConversationShellOptions _options;

        public ConversationShellEnvironmentProvider(ConversationShellOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            _options = options;
        }

        protected override async ValueTask<AIContext> ProvideAIContextAsync(
            InvokingContext context, CancellationToken cancellationToken = default)
        {
            if (TurnRegistry.For(context.Session)?.Shells is not { } shells)
            {
                return new AIContext();
            }

            ShellEnvironmentSnapshot snapshot = await shells.GetEnvironmentAsync(_options, cancellationToken).ConfigureAwait(false);

            return new AIContext
            {
                Instructions = ShellEnvironmentProvider.DefaultInstructionsFormatter(snapshot),
            };
        }
    }
}
