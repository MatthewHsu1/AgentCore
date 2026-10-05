using System.Runtime.CompilerServices;
using AgentCore.Application.Hooks.Engine;
using AgentCore.Application.Hooks.Gates;
using Microsoft.Extensions.AI;
using AgentCore.Application.Runtime.Turn;

namespace AgentCore.Application.Hooks.Layers
{
    /// <summary>
    /// BeforeCompaction at the one point that means "AgentCore is about to compact": the strategy's first request to the
    /// summariser. The chain runs once; its decision answers every request of this compaction.
    /// </summary>
    internal sealed class GatedSummariser(IChatClient inner, HookRuntime hooks, TurnInvocation? turn, int messageCount) : DelegatingChatClient(inner)
    {
        private sealed record Decision(bool Cancel, string? Summary);

        private Decision? _decision;

        /// <summary>Gets whether the strategy asked for a summary at all.</summary>
        internal bool Asked => _decision is not null;

        /// <summary>Gets whether a hook cancelled this compaction.</summary>
        internal bool Cancelled => _decision?.Cancel == true;

        public override async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            return await AnswerAsync(cancellationToken).ConfigureAwait(false)
                ?? await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        }

        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (await AnswerAsync(cancellationToken).ConfigureAwait(false) is { } answer)
            {
                foreach (ChatResponseUpdate update in answer.ToChatResponseUpdates())
                {
                    yield return update;
                }

                yield break;
            }

            await foreach (ChatResponseUpdate update in base.GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
            {
                yield return update;
            }
        }

        private async ValueTask<ChatResponse?> AnswerAsync(CancellationToken cancellationToken)
        {
            _decision ??= await DecideAsync(cancellationToken).ConfigureAwait(false);

            return _decision switch
            {
                { Cancel: true } => new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Empty)),
                { Summary: { } text } => new ChatResponse(new ChatMessage(ChatRole.Assistant, text)),
                _ => null,
            };
        }

        private async ValueTask<Decision> DecideAsync(CancellationToken cancellationToken)
        {
            if (turn?.Hooks is not { } raiser || !hooks.Gates.Overrides(GatePoint.BeforeCompaction))
            {
                return new Decision(Cancel: false, Summary: null);
            }

            HookScope scope = raiser.Scope(turn.TurnIndex, turn.Stage);
            IDictionary<string, object?> items = turn.Items ?? new Dictionary<string, object?>(StringComparer.Ordinal);

            return await hooks.Gates.RunAsync(
                GatePoint.BeforeCompaction,
                scope,
                new Decision(Cancel: false, Summary: null),
                _ => new CompactionGate(scope, messageCount, items),
                static (hook, gate, token) => hook.BeforeCompactionAsync(gate, token),
                static (state, gate) => new Decision(state.Cancel || gate.Cancelled, gate.Summary ?? state.Summary),
                static state => state with { Cancel = true },
                raiser.RaiseFault,
                cancellationToken).ConfigureAwait(false);
        }
    }
}
