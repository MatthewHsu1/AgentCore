using AgentCore.Application.Runtime.Turn;
using Microsoft.Agents.AI;

namespace AgentCore.Application.Runtime.Harness
{
    /// <summary>
    /// The context provider behind an agent's <c>shell:</c> block.
    /// </summary>
    internal sealed class ConversationShellProvider : AIContextProvider
    {
        private readonly ConversationShellOptions _options;

        public ConversationShellProvider(ConversationShellOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            _options = options;
        }

        // A delegated agent's run files no turn on its own session, so the shell has no workspace to
        // run in. The tool is simply absent rather than a thrown fault (design §6.3#6).
        protected override ValueTask<AIContext> ProvideAIContextAsync(
            InvokingContext context, CancellationToken cancellationToken = default)
        {
            ConversationShells? shells = TurnRegistry.For(context.Session)?.Shells;

            return new ValueTask<AIContext>(shells is null
                ? new AIContext()
                : new AIContext { Tools = [shells.Get(_options).AsAIFunction(requireApproval: false)] });
        }
    }
}
