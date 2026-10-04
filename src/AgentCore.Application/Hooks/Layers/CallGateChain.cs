using AgentCore.Application.Hooks.Engine;
using AgentCore.Application.Hooks.Gates;

namespace AgentCore.Application.Hooks.Layers
{
    /// <summary>What a transport offers when a call arrives.</summary>
    /// <param name="Entry">The entry the call's route serves.</param>
    /// <param name="CallId">The transport's id for the call.</param>
    /// <param name="From">The caller's number or address, when known.</param>
    /// <param name="To">The number or address that was called, when known.</param>
    /// <param name="Headers">The transport's headers, for example SIP headers.</param>
    /// <param name="Transport">The kind of transport that offered the call.</param>
    internal sealed record CallOffer(string Entry, string CallId, string? From, string? To, IReadOnlyDictionary<string, string> Headers, string Transport);

    /// <summary>The call gate's answer: accepted under a conversation id with an optional brief, or refused with a reason.</summary>
    /// <param name="Accepted">Whether the call is taken.</param>
    /// <param name="ConversationId">The conversation the call joins, when accepted.</param>
    /// <param name="Brief">Text the agent is told about the call, when accepted.</param>
    /// <param name="Refusal">Why the call is refused, when it is.</param>
    internal sealed record CallDecision(bool Accepted, string? ConversationId, string? Brief, CallRefusal? Refusal);

    /// <summary>The phone path calls this when a transport offers a call.</summary>
    internal static class CallGateChain
    {
        private static readonly CallDecision Undecided = new(Accepted: false, ConversationId: null, Brief: null, Refusal: null);

        /// <summary>Runs the chain and returns what the call does.</summary>
        /// <param name="hooks">The compiled hooks.</param>
        /// <param name="offer">The call on offer.</param>
        /// <param name="cancellationToken">The call's token.</param>
        /// <param name="deadline">How long each hook may take: providers.conversation.answerSeconds, or <see langword="null"/> for 5 s.</param>
        internal static async ValueTask<CallDecision> DecideAsync(HookRuntime hooks, CallOffer offer, CancellationToken cancellationToken, TimeSpan? deadline = null)
        {
            ArgumentNullException.ThrowIfNull(hooks);
            ArgumentNullException.ThrowIfNull(offer);

            if (!hooks.Gates.Overrides(GatePoint.BeforeCall))
            {
                return new CallDecision(Accepted: true, offer.CallId, Brief: null, Refusal: null);
            }

            HookScope scope = HookScopes.ForGate(conversationId: null, offer.Entry, hooks.Timers.GetUtcNow());

            CallDecision decision = await hooks.Gates.RunAsync(
                GatePoint.BeforeCall,
                scope,
                Undecided,
                _ => new CallGate(scope, offer.Entry, offer.CallId, offer.From, offer.To, offer.Headers, offer.Transport),
                static (hook, gate, token) => hook.BeforeCallAsync(gate, token),
                static (state, gate) =>
                {
                    if (gate.AcceptedConversationId is { } accepted)
                    {
                        return new CallDecision(Accepted: true, accepted, gate.Brief, Refusal: null);
                    }

                    return gate.Refusal is { } refusal ? new CallDecision(Accepted: false, ConversationId: null, Brief: null, refusal) : state;
                },
                static _ => new CallDecision(Accepted: false, ConversationId: null, Brief: null, CallRefusal.Unavailable),
                hooks.RaiseHostFault,
                cancellationToken,
                deadline).ConfigureAwait(false);

            return decision == Undecided ? decision with { Refusal = CallRefusal.Unavailable } : decision;
        }
    }
}
