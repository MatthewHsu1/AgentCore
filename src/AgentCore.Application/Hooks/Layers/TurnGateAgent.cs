using System.Runtime.CompilerServices;
using AgentCore.Application.Hooks.Engine;
using AgentCore.Application.Hooks.Gates;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using AgentCore.Application.Runtime.Turn;
using AgentCore.Application.Runtime.Turn.Lifecycle;

namespace AgentCore.Application.Hooks.Layers
{
    /// <summary>
    /// Runs between the seal and moderation. It runs once per conversation turn, never for a nested run, and before
    /// moderation and the model see the caller's words. A hook that fails closed blocks the turn with <c>refusalReply</c>.
    /// </summary>
    internal sealed class TurnGateAgent(AIAgent inner, HookRuntime hooks, string refusalReply) : DelegatingAIAgent(inner)
    {
        private sealed record Decision(string Text, string? Block, IReadOnlyList<string> Context);

        protected override async Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (Filed(options) is null)
            {
                return await base.RunCoreAsync(messages, session, options, cancellationToken).ConfigureAwait(false);
            }

            List<AgentResponseUpdate> updates = [];
            await foreach (AgentResponseUpdate update in RunCoreStreamingAsync(messages, session, options, cancellationToken).ConfigureAwait(false))
            {
                updates.Add(update);
            }

            return updates.ToAgentResponse();
        }

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (Filed(options) is not { } turn)
            {
                await foreach (AgentResponseUpdate update in base.RunCoreStreamingAsync(messages, session, options, cancellationToken).ConfigureAwait(false))
                {
                    yield return update;
                }

                yield break;
            }

            ChatMessage user = turn.User!;
            SessionHooks raiser = turn.Hooks!;
            HookScope scope = raiser.Scope(turn.TurnIndex, turn.Stage);
            IDictionary<string, object?> items = turn.Items ?? new Dictionary<string, object?>(StringComparer.Ordinal);

            Decision decision = await hooks.Gates.RunAsync(
                GatePoint.BeforeTurn,
                scope,
                new Decision(user.Text, Block: null, Context: []),
                state => new TurnGate(scope, state.Text, items),
                static (hook, gate, token) => hook.BeforeTurnAsync(gate, token),
                static (state, gate) => new Decision(gate.Input ?? state.Text, gate.BlockReply ?? state.Block, [.. state.Context, .. gate.AddedContext]),
                state => state with { Block = refusalReply },
                raiser.RaiseFault,
                cancellationToken).ConfigureAwait(false);

            if (decision.Block is { } reply)
            {
                AgentResponseUpdate blocked = new(ChatRole.Assistant, reply);
                (blocked.AdditionalProperties ??= []).Add(
                    new TurnDisposition(Moderation: null, FlaggedCategories: null, FallbackCause.None, FallbackFault: null, ModerationReason: null) { Blocked = true });
                yield return blocked;
                yield break;
            }

            if (!string.Equals(decision.Text, user.Text, StringComparison.Ordinal))
            {
                // In place: the seal commits this same message object (FiledTurn.User).
                user.Contents = [new TextContent(decision.Text), .. user.Contents.Where(static content => content is not TextContent)];
            }

            foreach (string note in decision.Context)
            {
                turn.AddedContext?.Enqueue(note);
            }

            await foreach (AgentResponseUpdate update in base.RunCoreStreamingAsync(messages, session, options, cancellationToken).ConfigureAwait(false))
            {
                yield return update;
            }
        }

        private static TurnInvocation? Filed(AgentRunOptions? options) =>
            TurnInvocation.From(options) is { Nested: false, User: not null, Hooks: not null } turn ? turn : null;
    }
}
