using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Hooks.Engine;
using AgentCore.Application.Hooks.Notices;
using Microsoft.Extensions.AI;
using AgentCore.Application.Runtime.Turn;

namespace AgentCore.Application.Hooks.Layers
{
    /// <summary>One model round trip of a turn, as the model layer reports it.</summary>
    internal sealed class ModelRound
    {
        private readonly long _started;

        internal ModelRound(SessionHooks hooks, TurnInvocation turn)
        {
            Hooks = hooks;
            Index = turn.Rounds?.Next() ?? 0;
            Scope = hooks.Scope(turn.TurnIndex, turn.Stage);
            Items = turn.Items ?? new Dictionary<string, object?>(StringComparer.Ordinal);
            RefusalReply = turn.RefusalReply ?? AgentCoreConfiguration.DefaultRefusalReply;
            _started = hooks.Time.GetTimestamp();
        }

        internal SessionHooks Hooks { get; }

        internal int Index { get; }

        internal HookScope Scope { get; }

        internal IDictionary<string, object?> Items { get; }

        /// <summary>Gets what a gate that fails closed answers in the model's place.</summary>
        internal string RefusalReply { get; }

        /// <summary>Raises <see cref="ModelCalled"/> for this round, when some hook wants it.</summary>
        internal void Called(ChatResponse? response, ChatOptions? options, Exception? failure)
        {
            if (!Hooks.Wants<ModelCalled>())
            {
                return;
            }

            UsageDetails? usage = response?.Usage;
            _ = Hooks.Raise(new ModelCalled(
                Scope,
                response?.ModelId ?? options?.ModelId,
                Index,
                usage?.InputTokenCount,
                usage?.OutputTokenCount,
                usage?.CachedInputTokenCount,
                Hooks.Time.GetElapsedTime(_started),
                response?.FinishReason?.Value,
                failure is null ? null : $"{failure.GetType().Name}: {failure.Message}"));
        }
    }
}
