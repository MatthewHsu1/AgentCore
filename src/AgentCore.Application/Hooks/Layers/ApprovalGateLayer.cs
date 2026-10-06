using System.Runtime.CompilerServices;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Hooks.Engine;
using AgentCore.Application.Hooks.Gates;
using AgentCore.Application.Hooks.Notices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using AgentCore.Application.Runtime.Turn;

namespace AgentCore.Application.Hooks.Layers
{
    /// <summary>The combined auto-approval rule, and the deny layer outside the approval layer.</summary>
    internal static class ApprovalGateLayer
    {
        /// <summary>
        /// The most model rounds of one run whose denials the deny layer answers. A run that only hands back a request MAF
        /// already queued calls no model and does not count. The next model round that still asks for a denied call ends
        /// the run as a fault.
        /// </summary>
        internal const int MaxRounds = 8;

        /// <summary>What the model is told when a hook that fails closed broke at BeforeToolApproval.</summary>
        private const string FailedClosedReason = "a hook denied this call.";

        /// <summary>
        /// The one rule <c>UseToolApproval</c> gets: the hook chain first, then the document's patterns.
        /// <see langword="true"/> approves; <see langword="false"/> asks, or leaves a denial for the deny layer.
        /// </summary>
        internal static Func<ToolAutoApprovalRuleContext, ValueTask<bool>> Rule(IReadOnlyList<string> auto, HookRuntime hooks)
        {
            return async context =>
            {
                FunctionCallContent call = context.FunctionCallContent;
                TurnInvocation? turn = TurnInvocation.From(context.RunOptions);
                SessionHooks? session = turn?.Hooks;

                if (session is not null && hooks.Gates.Overrides(GatePoint.BeforeToolApproval))
                {
                    ApprovalDecision decision = session.ApprovalDecisions.TryGetValue(call.CallId, out ApprovalDecision? known)
                        ? known
                        : await DecideAsync(hooks, session, turn!, call).ConfigureAwait(false);

                    if (decision.Approved is { } approved)
                    {
                        return approved;
                    }
                }

                if (auto.Any(pattern => AgentApproval.Matches(pattern, call.Name)))
                {
                    if (session is not null)
                    {
                        _ = session.ApprovalDecisions.TryRemove(call.CallId, out _);
                        Changed(session, session.Scope(turn!.TurnIndex, turn.Stage), call, ApprovalState.Approved, ApprovalBy.Rule, reason: null);
                    }

                    return true;
                }

                return false;
            };
        }

        /// <summary>Adds the deny layer. Registered before <c>UseToolApproval</c>, so it is the outer one.</summary>
        internal static AIAgentBuilder UseDenials(AIAgentBuilder builder)
        {
            return builder.Use(
                runFunc: static (messages, session, options, inner, cancellationToken) => RunAsync(messages, session, options, inner, cancellationToken),
                runStreamingFunc: static (messages, session, options, inner, cancellationToken) => StreamAsync(messages, session, options, inner, cancellationToken));
        }

        // Runs the chain once per call. An approval is settled the moment the rule returns true, so only a denial and a
        // call left undecided are kept, for MAF's next question about the same call.
        private static async ValueTask<ApprovalDecision> DecideAsync(HookRuntime hooks, SessionHooks session, TurnInvocation turn, FunctionCallContent call)
        {
            HookScope scope = session.Scope(turn.TurnIndex, turn.Stage);
            IReadOnlyDictionary<string, object?> arguments = call.Arguments is { } given
                ? new Dictionary<string, object?>(given, StringComparer.Ordinal)
                : new Dictionary<string, object?>(StringComparer.Ordinal);
            IDictionary<string, object?> items = turn.Items ?? new Dictionary<string, object?>(StringComparer.Ordinal);

            // The rule has no CancellationToken; the gate's own deadline still bounds the wait.
            ApprovalDecision decision = await hooks.Gates.RunAsync(
                GatePoint.BeforeToolApproval,
                scope,
                new ApprovalDecision(Approved: null, Reason: null),
                _ => new ApprovalGate(scope, call.Name, call.CallId, arguments, items),
                static (hook, gate, token) => hook.BeforeToolApprovalAsync(gate, token),
                static (state, gate) => gate.Approved is null ? state : new ApprovalDecision(gate.Approved, gate.DenyReason),
                static _ => new ApprovalDecision(Approved: false, Reason: FailedClosedReason),
                session.RaiseFault,
                CancellationToken.None).ConfigureAwait(false);

            if (decision.Approved is false)
            {
                decision = decision with { Reason = decision.Reason ?? FailedClosedReason };
                Changed(session, scope, call, ApprovalState.Denied, ApprovalBy.Hook, decision.Reason);
            }
            else if (decision.Approved is true)
            {
                Changed(session, scope, call, ApprovalState.Approved, ApprovalBy.Hook, reason: null);
                return decision;
            }

            session.ApprovalDecisions[call.CallId] = decision;
            return decision;
        }

        private static void Changed(SessionHooks hooks, HookScope scope, FunctionCallContent call, ApprovalState state, ApprovalBy by, string? reason)
        {
            if (hooks.Wants<ApprovalChanged>())
            {
                _ = hooks.Raise(new ApprovalChanged(scope, call.Name, call.CallId, state, by, reason));
            }
        }

        /// <summary>The requests among <paramref name="contents"/> a BeforeToolApproval hook denied and the layer has not answered yet, each with its reason.</summary>
        private static List<(ToolApprovalRequestContent Request, string Reason)> DeniedOf(IEnumerable<AIContent> contents, AgentRunOptions? options)
        {
            if (TurnInvocation.From(options)?.Hooks is not { } session)
            {
                return [];
            }

            List<(ToolApprovalRequestContent Request, string Reason)> denied = [];
            foreach (ToolApprovalRequestContent request in contents.OfType<ToolApprovalRequestContent>())
            {
                if (request.ToolCall is FunctionCallContent call
                    && session.ApprovalDecisions.TryGetValue(call.CallId, out ApprovalDecision? decision)
                    && decision is { Approved: false, Reason: { } reason })
                {
                    denied.Add((request, reason));
                }
            }

            return denied;
        }

        /// <summary>The contents with the denied requests taken out.</summary>
        private static List<AIContent> Without(IList<AIContent> contents, List<(ToolApprovalRequestContent Request, string Reason)> denied)
        {
            return [.. contents.Where(content =>
                content is not ToolApprovalRequestContent request || !denied.Exists(pair => ReferenceEquals(pair.Request, request)))];
        }

        /// <summary>Answers the denials, settling them: the session forgets them and the turn knows their calls were refused.</summary>
        private static ChatMessage Answer(List<(ToolApprovalRequestContent Request, string Reason)> denied, AgentRunOptions? options)
        {
            TurnInvocation? turn = TurnInvocation.From(options);
            foreach ((ToolApprovalRequestContent request, _) in denied)
            {
                string callId = ((FunctionCallContent)request.ToolCall).CallId;
                _ = turn?.Hooks?.ApprovalDecisions.TryRemove(callId, out _);
                turn?.Results?.NoteDenied(callId);
            }

            return new(ChatRole.User, [.. denied.Select(static pair => (AIContent)pair.Request.CreateResponse(false, pair.Reason))]);
        }

        private static int? RoundsSoFar(AgentRunOptions? options)
        {
            return TurnInvocation.From(options)?.Rounds?.Count;
        }

        // Without a round counter every run counts, which can only end the run sooner.
        private static bool CalledTheModel(int? before, AgentRunOptions? options)
        {
            return before is null || RoundsSoFar(options) != before;
        }

        /// <summary>The cap is reached with a denial still open: never a person's to answer. Logs, raises the fault, and throws.</summary>
        private static InvalidOperationException Exhausted(List<(ToolApprovalRequestContent Request, string Reason)> denied, AgentRunOptions? options)
        {
            string message = $"A hook denied tool calls after {MaxRounds} model rounds of denials in one turn, so the turn ends without asking a person.";
            if (TurnInvocation.From(options) is { Hooks: { } session } turn)
            {
                foreach ((ToolApprovalRequestContent request, _) in denied)
                {
                    _ = session.ApprovalDecisions.TryRemove(((FunctionCallContent)request.ToolCall).CallId, out _);
                }

                HookLog.DenialRoundsExhausted(session.Runtime.Logger, session.ConversationId, turn.TurnIndex, MaxRounds);
                _ = session.Raise(new Fault(session.Scope(turn.TurnIndex, turn.Stage), FaultKind.DenialRoundsExhausted, message, Cause: null));
            }

            return new InvalidOperationException(message);
        }

        private static async Task<AgentResponse> RunAsync(
            IEnumerable<ChatMessage> messages, AgentSession? session, AgentRunOptions? options, AIAgent inner, CancellationToken cancellationToken)
        {
            session ??= await inner.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
            IEnumerable<ChatMessage> input = messages;
            List<ChatMessage> all = [];
            int answered = 0;

            while (true)
            {
                int? before = RoundsSoFar(options);
                AgentResponse response = await inner.RunAsync(input, session, options, cancellationToken).ConfigureAwait(false);
                List<(ToolApprovalRequestContent Request, string Reason)> denied = DeniedOf(response.Messages.SelectMany(static message => message.Contents), options);

                if (denied.Count == 0)
                {
                    all.AddRange(response.Messages);
                    response.Messages = all;
                    return response;
                }

                if (CalledTheModel(before, options) && answered++ == MaxRounds)
                {
                    throw Exhausted(denied, options);
                }

                foreach (ChatMessage message in response.Messages)
                {
                    List<AIContent> kept = Without(message.Contents, denied);
                    if (kept.Count > 0)
                    {
                        all.Add(new ChatMessage(message.Role, kept) { MessageId = message.MessageId, AuthorName = message.AuthorName });
                    }
                }

                input = [Answer(denied, options)];
            }
        }

        // Only updates that carry an approval request are held back; everything else streams through at once.
        private static async IAsyncEnumerable<AgentResponseUpdate> StreamAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options,
            AIAgent inner,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            session ??= await inner.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
            IEnumerable<ChatMessage> input = messages;
            int answered = 0;

            while (true)
            {
                int? before = RoundsSoFar(options);
                List<AgentResponseUpdate> held = [];
                await foreach (AgentResponseUpdate update in inner.RunStreamingAsync(input, session, options, cancellationToken).ConfigureAwait(false))
                {
                    if (update.Contents.OfType<ToolApprovalRequestContent>().Any())
                    {
                        held.Add(update);
                        continue;
                    }

                    yield return update;
                }

                List<(ToolApprovalRequestContent Request, string Reason)> denied = DeniedOf(held.SelectMany(static update => update.Contents), options);

                if (denied.Count == 0)
                {
                    foreach (AgentResponseUpdate update in held)
                    {
                        yield return update;
                    }

                    yield break;
                }

                if (CalledTheModel(before, options) && answered++ == MaxRounds)
                {
                    throw Exhausted(denied, options);
                }

                foreach (AgentResponseUpdate update in held)
                {
                    List<AIContent> rest = Without(update.Contents, denied);
                    if (rest.Count > 0)
                    {
                        update.Contents = rest;
                        yield return update;
                    }
                }

                input = [Answer(denied, options)];
            }
        }
    }
}
