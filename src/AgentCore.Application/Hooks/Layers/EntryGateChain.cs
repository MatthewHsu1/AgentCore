using System.Security.Claims;
using AgentCore.Application.Hooks.Engine;
using AgentCore.Application.Hooks.Gates;

namespace AgentCore.Application.Hooks.Layers
{
    /// <summary>What a request on an AgentCore route offers the entry gate.</summary>
    /// <param name="User">The authenticated user, or <see langword="null"/> when the request is anonymous.</param>
    /// <param name="Headers">The HTTP request's headers. A header with several values joins them with a comma.</param>
    /// <param name="Route">The route pattern that matched.</param>
    /// <param name="RequestedEntry">The entry the URL named, or <see langword="null"/> when the route has no entry segment.</param>
    /// <param name="Transport">How the request arrived.</param>
    internal sealed record EntryOffer(ClaimsPrincipal? User, IReadOnlyDictionary<string, string> Headers, string Route, string? RequestedEntry, EntryTransport Transport);

    /// <summary>Picks the entry a request runs. No decision takes the URL's entry; none there refuses.</summary>
    internal static class EntryGateChain
    {
        private sealed record Choice(string? Entry, bool Refused);

        /// <summary>Runs the chain and returns the entry the request runs. With no hook overriding BeforeEntry it returns the URL's entry.</summary>
        /// <param name="hooks">The compiled hooks.</param>
        /// <param name="offer">The request on offer.</param>
        /// <param name="cancellationToken">The request's token.</param>
        /// <returns>The entry key, or <see langword="null"/> when a hook refused or no entry is named.</returns>
        internal static async ValueTask<string?> ChooseAsync(HookRuntime hooks, EntryOffer offer, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(hooks);
            ArgumentNullException.ThrowIfNull(offer);

            HookScope scope = HookScopes.ForGate(conversationId: null, offer.RequestedEntry, hooks.Timers.GetUtcNow());
            Choice choice = await hooks.Gates.RunAsync(
                GatePoint.BeforeEntry,
                scope,
                new Choice(Entry: null, Refused: false),
                _ => new EntryGate(scope, offer.User, offer.Headers, offer.Route, offer.RequestedEntry, offer.Transport),
                static (hook, gate, token) => hook.BeforeEntryAsync(gate, token),
                static (state, gate) =>
                {
                    if (gate.Chosen is { } chosen)
                    {
                        return new Choice(chosen, Refused: false);
                    }

                    return gate.Refused ? new Choice(Entry: null, Refused: true) : state;
                },
                static _ => new Choice(Entry: null, Refused: true),
                hooks.RaiseHostFault,
                cancellationToken).ConfigureAwait(false);

            return choice.Refused ? null : choice.Entry ?? offer.RequestedEntry;
        }
    }
}
