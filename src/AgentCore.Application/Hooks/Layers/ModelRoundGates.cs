using AgentCore.Application.Hooks.Engine;
using AgentCore.Application.Hooks.Gates;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Hooks.Layers
{
    /// <summary>The three model gate chains (BeforeModel, AfterModel, AfterModelFailed) over one round.</summary>
    internal static class ModelRoundGates
    {
        internal static ValueTask<Asked> BeforeAsync(
            GateRunner gates, ModelRound round, IEnumerable<ChatMessage> messages, ChatOptions? options, CancellationToken cancellationToken)
        {
            Asked asked = new(messages as IReadOnlyList<ChatMessage> ?? [.. messages], options, Answer: null);
            return !gates.Overrides(GatePoint.BeforeModel)
                ? ValueTask.FromResult(asked)
                : gates.RunAsync(
                    GatePoint.BeforeModel,
                    round.Scope,
                    asked,
                    state => new ModelGate(round.Scope, round.Index, state.Messages, state.Options, round.Items),
                    static (hook, gate, token) => hook.BeforeModelAsync(gate, token),
                    static (state, gate) => new Asked(gate.NewMessages ?? state.Messages, gate.NewOptions ?? state.Options, gate.Response ?? state.Answer),
                    state => state with { Answer = Refusal(round) },
                    round.Hooks.RaiseFault,
                    cancellationToken);
        }

        internal static ValueTask<ChatResponse> AfterAsync(GateRunner gates, ModelRound round, ChatResponse response, CancellationToken cancellationToken)
        {
            return !gates.Overrides(GatePoint.AfterModel)
                ? ValueTask.FromResult(response)
                : gates.RunAsync(
                    GatePoint.AfterModel,
                    round.Scope,
                    response,
                    state => new ModelResultGate(round.Scope, round.Index, state, round.Items),
                    static (hook, gate, token) => hook.AfterModelAsync(gate, token),
                    static (state, gate) => gate.Replacement ?? state,
                    _ => Refusal(round),
                    round.Hooks.RaiseFault,
                    cancellationToken);
        }

        internal static ValueTask<Recovery> FailedAsync(
            GateRunner gates, ModelRound round, int attempt, Exception failure, bool canRetry, CancellationToken cancellationToken)
        {
            Recovery none = new(Retrying: false, Answer: null);
            return !gates.Overrides(GatePoint.AfterModelFailed)
                ? ValueTask.FromResult(none)
                : gates.RunAsync(
                    GatePoint.AfterModelFailed,
                    round.Scope,
                    none,
                    _ => new ModelFailureGate(round.Scope, round.Index, attempt, failure, canRetry, round.Items),
                    static (hook, gate, token) => hook.AfterModelFailedAsync(gate, token),
                    static (state, gate) => new Recovery(gate.Retrying, gate.Response ?? state.Answer),
                    static state => state,
                    round.Hooks.RaiseFault,
                    cancellationToken);
        }

        private static ChatResponse Refusal(ModelRound round) => new(new ChatMessage(ChatRole.Assistant, round.RefusalReply));

        /// <summary>What the model is asked after BeforeModel, or the answer a hook gave in its place.</summary>
        internal sealed record Asked(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options, ChatResponse? Answer);

        /// <summary>What AfterModelFailed decided: call the model again, or answer for it.</summary>
        internal sealed record Recovery(bool Retrying, ChatResponse? Answer);
    }
}
