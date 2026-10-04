using AgentCore.Application.Conversation;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Hooks;
using AgentCore.Application.Hooks.Engine;
using AgentCore.Application.Ports;
using AgentCore.Application.State;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using AgentCore.Application.Runtime.ToolCalls;

namespace AgentCore.Application.Runtime.Session
{
    /// <summary>
    /// Builds one <see cref="ConversationSession"/> for each conversation, over one compiled agent.
    /// </summary>
    public sealed class ConversationSessionFactory : IConversationSessionFactory, IAsyncDisposable
    {
        private readonly ConversationSessionSeams _seams;

        private readonly string? _workspaceRoot;

        private readonly HookRuntime? _ownedHooks;

        /// <summary>
        /// Creates the factory. When <paramref name="workspaceRoot"/> is bound, every conversation this factory
        /// builds gets its own folder under it; unbound, a session it builds has no workspace.
        /// </summary>
        /// <param name="compiled">The compiled entry every session runs.</param>
        /// <param name="guards">The evaluator that runs each exit guard.</param>
        /// <param name="extractor">The state extractor the document declares, or <see langword="null"/>.</param>
        /// <param name="timeProvider">The clock every turn reads, or <see langword="null"/> for the system clock.</param>
        /// <param name="logger">Where the sessions' own log events go, or <see langword="null"/> for none.</param>
        /// <param name="hooks">
        /// Notice hooks of this factory's sessions, heard after the hooks the agent was compiled with, or
        /// <see langword="null"/>. A hook that overrides a gate must be compiled in (<see cref="AgentCompilationContext.Hooks"/>).
        /// A factory with hooks of its own has its own notice delivery: its sessions count <see cref="HookScope.Sequence"/>
        /// per conversation id apart from the compiled hooks' count, so a conversation id that sessions of both open
        /// gets two separate sequences.
        /// </param>
        /// <param name="workspaceRoot">The root every conversation's folder is made under, or <see langword="null"/> for no workspace.</param>
        /// <exception cref="ArgumentException">A hook in <paramref name="hooks"/> overrides a gate and was not compiled in.</exception>
        public ConversationSessionFactory(
            CompiledAgent compiled,
            IGuardEvaluator guards,
            StateExtractor? extractor = null,
            TimeProvider? timeProvider = null,
            ILogger? logger = null,
            IEnumerable<AgentHook>? hooks = null,
            string? workspaceRoot = null)
        {
            ArgumentNullException.ThrowIfNull(compiled);
            ArgumentNullException.ThrowIfNull(guards);

            _ownedHooks = hooks is null ? null : compiled.Hooks.WithNoticeHooks(hooks);
            _seams = new ConversationSessionSeams(
                compiled, guards, extractor, timeProvider ?? TimeProvider.System, logger, _ownedHooks ?? compiled.Hooks);
            _workspaceRoot = workspaceRoot;
        }

        /// <summary>Builds the extractor one document declares.</summary>
        /// <param name="compiled">The compiled agent.</param>
        /// <param name="chatClients">The seam that resolves <c>extractor.model</c>.</param>
        /// <returns>The extractor, or <see langword="null"/> when the document declares none.</returns>
        public static StateExtractor? CreateExtractor(CompiledAgent compiled, IChatClientFactory chatClients)
        {
            ArgumentNullException.ThrowIfNull(compiled);
            ArgumentNullException.ThrowIfNull(chatClients);

            return compiled.Configuration.Extractor is { } declared
                ? new StateExtractor(
                    compiled.Configuration,
                    chatClients.GetChatClient(declared.Model)
                               .AsBuilder()
                               .UseOpenTelemetry(configure: static client => client.EnableSensitiveData = false)
                               .Use(static innerClient => new ModelFacingChatClient(innerClient))
                               .Build())
                : null;
        }

        /// <summary>
        /// Stops the notice delivery this factory started for its own hooks, waiting for the queued notices. A factory
        /// built without hooks owns nothing and stops nothing: the compile's hooks belong to the host.
        /// </summary>
        public ValueTask DisposeAsync()
        {
            return _ownedHooks?.DisposeAsync() ?? ValueTask.CompletedTask;
        }

        /// <inheritdoc />
        public ConversationSession Create(string? conversationId = null, ConversationSessionState? state = null)
        {
            string resolvedConversationId = string.IsNullOrWhiteSpace(conversationId) ? Guid.NewGuid().ToString("N") : conversationId;

            ConversationWorkspace? workspace = _workspaceRoot is null
                ? null
                : ConversationWorkspace.Create(_workspaceRoot, resolvedConversationId, _seams.Time, _seams.Logger);

            ConversationSession session = new(resolvedConversationId, _seams, workspace);

            // Named, not applied. The session resumes on its first turn, where the stored copy outranks this one.
            if (state is not null)
            {
                session.States.Resume(state);
            }

            return session;
        }
    }
}
