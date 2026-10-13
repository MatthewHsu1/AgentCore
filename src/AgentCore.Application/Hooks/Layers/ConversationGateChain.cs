using System.Security.Claims;
using AgentCore.Application.Hooks.Engine;
using AgentCore.Application.Hooks.Gates;

namespace AgentCore.Application.Hooks.Layers
{
    /// <summary>What a request offers the conversation gate.</summary>
    /// <param name="User">The authenticated user, or <see langword="null"/> when the request is anonymous.</param>
    /// <param name="Headers">The HTTP request's headers.</param>
    /// <param name="Route">The route pattern that matched.</param>
    /// <param name="Entry">The entry the request runs.</param>
    /// <param name="ConversationId">The conversation the request names.</param>
    /// <param name="Transport">How the request arrived.</param>
    /// <param name="Resuming">Whether the request answers an approval.</param>
    internal sealed record ConversationOffer(
        ClaimsPrincipal? User,
        IReadOnlyDictionary<string, string> Headers,
        string Route,
        string Entry,
        string ConversationId,
        EntryTransport Transport,
        bool Resuming);

    /// <summary>Asks the hooks whether a request may touch the conversation it names. No hook means yes.</summary>
    internal static class ConversationGateChain
    {
        /// <summary>Runs the chain.</summary>
        /// <param name="hooks">The compiled hooks.</param>
        /// <param name="offer">The request on offer.</param>
        /// <param name="cancellationToken">The request's token.</param>
        /// <returns><see langword="true"/> when no hook refused and none failed closed.</returns>
        internal static async ValueTask<bool> AllowAsync(HookRuntime hooks, ConversationOffer offer, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(hooks);
            ArgumentNullException.ThrowIfNull(offer);

            if (!hooks.Gates.Overrides(GatePoint.BeforeConversation))
            {
                return true;
            }

            HookScope scope = HookScopes.ForGate(offer.ConversationId, offer.Entry, hooks.Timers.GetUtcNow());

            bool refused = await hooks.Gates.RunAsync(
                GatePoint.BeforeConversation,
                scope,
                false,
                _ => new ConversationGate(scope, offer.User, offer.Headers, offer.Route, offer.Entry, offer.ConversationId, offer.Transport, offer.Resuming),
                static (hook, gate, token) => hook.BeforeConversationAsync(gate, token),
                static (state, gate) => state || gate.Refused,
                static _ => true,
                hooks.RaiseHostFault,
                cancellationToken).ConfigureAwait(false);

            return !refused;
        }
    }
}
