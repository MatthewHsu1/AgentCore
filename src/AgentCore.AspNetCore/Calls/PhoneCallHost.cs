using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Conversation.Commands;
using AgentCore.Application.Hooks;
using AgentCore.Application.Hooks.BuiltIn;
using AgentCore.Application.Hooks.Engine;
using AgentCore.Application.Ports;
using AgentCore.AspNetCore.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentCore.AspNetCore.Calls
{
    /// <summary>What every call of one host shares.</summary>
    /// <param name="Sessions">The one session owner of the host.</param>
    /// <param name="Hooks">The compiled hooks: the call gate and the notices.</param>
    /// <param name="Time">The host's clock.</param>
    /// <param name="Logger">Where the call core logs.</param>
    /// <param name="AnswerDeadline">How long each BeforeCall hook may take: <c>providers.conversation.answerSeconds</c>, else 5 s.</param>
    internal sealed record PhoneCallHost(
        IConversationSessions Sessions,
        HookRuntime Hooks,
        TimeProvider Time,
        ILogger Logger,
        TimeSpan AnswerDeadline)
    {
        /// <summary>Gets the built-in brief hook, or <see langword="null"/> in a host that registered no conversation adapter.</summary>
        internal CallBriefHook? Briefs { get; } = Hooks.Table.Hooks.OfType<CallBriefHook>().FirstOrDefault();

        /// <summary>
        /// Gets whether the vendor speaks for itself, so every turn of its calls is told the
        /// <see cref="CallBriefHook.FrontVoiceNote"/>.
        /// </summary>
        internal bool FrontVoice { get; init; }

        /// <summary>Gets the root services, where the host's command handlers are found, or <see langword="null"/> for none.</summary>
        internal IServiceProvider? Services { get; init; }

        /// <summary>
        /// Gets the host's handler for one command, or <see langword="null"/> when it registered none. It comes from the
        /// root services: a call outlives the request that started it, so a scoped one would be used after its scope ended.
        /// </summary>
        internal IChannelCommandHandler<TCommand, TOutcome>? HandlerFor<TCommand, TOutcome>()
            where TCommand : ChannelCommand
        {
            return Services?.GetService<IChannelCommandHandler<TCommand, TOutcome>>();
        }

        /// <summary>Reads the host's shared parts from a started container.</summary>
        internal static PhoneCallHost From(IServiceProvider services, ConversationProviderConfiguration? configuration)
        {
            ArgumentNullException.ThrowIfNull(services);

            AgentCoreBoot boot = services.GetRequiredService<AgentCoreBoot>();
            return new PhoneCallHost(
                boot.Entries.Sessions,
                boot.Hooks,
                services.GetRequiredService<TimeProvider>(),
                services.GetRequiredService<ILoggerFactory>().CreateLogger("AgentCore.Calls"),
                configuration?.AnswerSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : GatePoint.BeforeCall.Deadline)
            {
                Services = boot.Services,
            };
        }
    }
}
