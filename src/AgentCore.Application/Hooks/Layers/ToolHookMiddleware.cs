using System.Globalization;
using System.Text.Json;
using AgentCore.Application.Hooks.Engine;
using AgentCore.Application.Hooks.Gates;
using AgentCore.Application.Hooks.Notices;
using AgentCore.Application.Skills;
using AgentCore.Application.Tools;
using AgentCore.Domain.Audit;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using AgentCore.Application.Runtime.Session;
using AgentCore.Application.Runtime.ToolCalls;
using AgentCore.Application.Runtime.Turn;

namespace AgentCore.Application.Hooks.Layers
{
    /// <summary>
    /// The one function middleware of an agent: BeforeTool, AfterTool and AfterToolFailed, and one <see cref="ToolCalled"/> per call.
    /// MAF runs function middleware innermost-first, so AgentCore registers this one and orders the hooks itself.
    /// </summary>
    internal static class ToolHookMiddleware
    {
        /// <summary>The most times one call is run again after a hook's <c>Retry</c>.</summary>
        internal const int MaxRetries = 2;

        private static readonly string StoppedAtEnd = string.Create(
            CultureInfo.InvariantCulture, $"stopped {ConversationEnding.ToolGrace.TotalSeconds:0} s after the conversation ended.");

        internal static AIAgent Apply(AIAgent agent, HookRuntime hooks)
        {
            AIAgent gated = new AIAgentBuilder(agent)
                .Use((_, context, next, cancellationToken) => InvokeAsync(hooks.Gates, context, next, cancellationToken))
                .Build();

            // MAF 1.21.0's function middleware wraps the ChatClientFactory of the options it is handed, in place.
            // LoopAgent hands every round the same options, so without a fresh copy per run round k would run
            // this middleware k times per call.
            return new AIAgentBuilder(gated)
                .Use(
                    (messages, session, options, inner, cancellationToken) => inner.RunAsync(messages, session, Fresh(options), cancellationToken),
                    (messages, session, options, inner, cancellationToken) => inner.RunStreamingAsync(messages, session, Fresh(options), cancellationToken))
                .Build();
        }

        private static AgentRunOptions? Fresh(AgentRunOptions? options)
        {
            return options is ChatClientAgentRunOptions run ? run.Clone() : options;
        }

        private sealed record Before(Dictionary<string, object?> Arguments, bool Blocked, bool Responded, object? Result, bool EndLoop);

        private sealed record After(object? Result, bool EndLoop);

        private sealed record Failed(bool Retrying, bool Responded, object? Result);

        /// <summary>What every gate and the notice of one call share.</summary>
        private sealed record Call(
            GateRunner Gates,
            SessionHooks Hooks,
            HookScope Scope,
            string Name,
            string CallId,
            IDictionary<string, object?> Items,
            long Started);

        private static async ValueTask<object?> InvokeAsync(
            GateRunner gates,
            FunctionInvocationContext context,
            Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next,
            CancellationToken cancellationToken)
        {
            TurnInvocation? turn = TurnInvocation.FiledIn(context.Arguments) ?? TurnInvocation.From(context.Options);
            if (turn?.Hooks is not { } hooks)
            {
                return await next(context, cancellationToken).ConfigureAwait(false);
            }

            Call call = new(
                gates,
                hooks,
                hooks.Scope(turn.TurnIndex, turn.Stage),
                context.CallContent.Name,
                context.CallContent.CallId,
                turn.Items ?? new Dictionary<string, object?>(StringComparer.Ordinal),
                hooks.Time.GetTimestamp());

            if (gates.Overrides(GatePoint.BeforeTool))
            {
                Dictionary<string, object?> asked = new(context.Arguments, StringComparer.Ordinal);
                Before before = await BeforeAsync(call, asked, cancellationToken).ConfigureAwait(false);
                context.Terminate |= before.EndLoop;

                if (before.Blocked || before.Responded)
                {
                    Called(call, before.Blocked ? ToolOutcome.Blocked : ToolOutcome.Responded, fatal: false, kind: null, failure: null);
                    return before.Result;
                }

                if (!ReferenceEquals(before.Arguments, asked))
                {
                    context.Arguments = Replaced(context.Arguments, before.Arguments);
                }
            }

            object? result;
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    result = await next(context, cancellationToken).ConfigureAwait(false);
                    break;
                }
                catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    Failed failed = await FailedAsync(call, attempt, exception, cancellationToken).ConfigureAwait(false);
                    if (failed.Retrying)
                    {
                        continue;
                    }

                    string message = $"{exception.GetType().Name}: {exception.Message}";
                    if (failed.Responded)
                    {
                        Called(call, ToolOutcome.Responded, fatal: false, ToolFailureKind.Faulted, message);
                        return failed.Result;
                    }

                    Called(call, ToolOutcome.Failed, AuditingFunctionInvokingChatClient.IsBeyondTheModel(exception), ToolFailureKind.Faulted, message);
                    throw;
                }
                catch (OperationCanceledException) when (cancellationToken == turn.ToolStop)
                {
                    // The end backstop stopped a call that ran: the audit shows it.
                    Called(call, ToolOutcome.Failed, fatal: true, ToolFailureKind.Faulted, StoppedAtEnd);
                    throw;
                }
            }

            if (gates.Overrides(GatePoint.AfterTool))
            {
                After after = await AfterAsync(call, result, cancellationToken).ConfigureAwait(false);
                result = after.Result;
                context.Terminate |= after.EndLoop;
            }

            ToolOutcome outcome = ToolCallOutcomes.Of(context.Arguments);
            Called(call, outcome, fatal: false, kind: null, outcome == ToolOutcome.TimedOut ? "the tool did not answer within its time limit." : null);
            RaiseSkillLoaded(call, context.Arguments, result);

            return result;
        }

        private static ValueTask<Before> BeforeAsync(Call call, Dictionary<string, object?> asked, CancellationToken cancellationToken)
        {
            return call.Gates.RunAsync(
                GatePoint.BeforeTool,
                call.Scope,
                new Before(asked, Blocked: false, Responded: false, Result: null, EndLoop: false),
                state => new ToolGate(call.Scope, call.Name, call.CallId, state.Arguments, call.Items),
                static (hook, gate, token) => hook.BeforeToolAsync(gate, token),
                static (state, gate) => state with
                {
                    // A copy: the hook keeps its own dictionary and may change it after the gate closed.
                    Arguments = gate.NewArguments is { } replaced ? new Dictionary<string, object?>(replaced, StringComparer.Ordinal) : state.Arguments,
                    Blocked = gate.Blocked,
                    Responded = gate.Responded,
                    Result = gate.Blocked || gate.Responded ? gate.Result : state.Result,
                    EndLoop = state.EndLoop || gate.EndsLoop,
                },
                state => state with { Blocked = true, Result = ToolErrorResult.Create(call.Name, "a hook refused this call.") },
                call.Hooks.RaiseFault,
                cancellationToken);
        }

        private static async ValueTask<Failed> FailedAsync(Call call, int attempt, Exception exception, CancellationToken cancellationToken)
        {
            if (!call.Gates.Overrides(GatePoint.AfterToolFailed))
            {
                return new Failed(Retrying: false, Responded: false, Result: null);
            }

            return await call.Gates.RunAsync(
                GatePoint.AfterToolFailed,
                call.Scope,
                new Failed(Retrying: false, Responded: false, Result: null),
                _ => new ToolFailureGate(call.Scope, call.Name, call.CallId, attempt, exception, attempt < MaxRetries, call.Items),
                static (hook, gate, token) => hook.AfterToolFailedAsync(gate, token),
                static (state, gate) => new Failed(gate.Retrying, gate.Responded, gate.Responded ? gate.Result : state.Result),
                static state => state,
                call.Hooks.RaiseFault,
                cancellationToken).ConfigureAwait(false);
        }

        private static ValueTask<After> AfterAsync(Call call, object? result, CancellationToken cancellationToken)
        {
            return call.Gates.RunAsync(
                GatePoint.AfterTool,
                call.Scope,
                new After(result, EndLoop: false),
                state => new ToolResultGate(call.Scope, call.Name, call.CallId, state.Result, call.Items),
                static (hook, gate, token) => hook.AfterToolAsync(gate, token),
                static (state, gate) => new After(gate.Replaced ? gate.NewResult : state.Result, state.EndLoop || gate.EndsLoop),
                state => state with { Result = "a hook withheld this result." },
                call.Hooks.RaiseFault,
                cancellationToken);
        }

        private static void Called(Call call, ToolOutcome outcome, bool fatal, ToolFailureKind? kind, string? failure)
        {
            if (call.Hooks.Wants<ToolCalled>())
            {
                _ = call.Hooks.Raise(new ToolCalled(
                    call.Scope, call.Name, call.CallId, call.Hooks.Time.GetElapsedTime(call.Started), outcome, fatal, kind, failure));
            }
        }

        // A load_skill the pinned-skill redirect answered, or that MAF answered "not
        // found", loaded nothing.
        private static void RaiseSkillLoaded(Call call, AIFunctionArguments arguments, object? result)
        {
            if (string.Equals(call.Name, AgentSkillsProvider.LoadSkillToolName, StringComparison.Ordinal)
                && !ToolCallOutcomes.Marked(arguments, ToolCallOutcomes.RedirectedKey)
                && PinnedSkillRedirect.ReadName(arguments) is { } skill
                && !SkillNotFound(skill, result)
                && call.Hooks.Wants<SkillLoaded>())
            {
                _ = call.Hooks.Raise(new SkillLoaded(call.Scope, skill, Pinned: false));
            }
        }

        // MAF's load_skill answers an unknown name with this text (SkillsProviderFactoryTests pins it), serialized.
        private static bool SkillNotFound(string skill, object? result)
        {
            return result is JsonElement { ValueKind: JsonValueKind.String } text
            && string.Equals(text.GetString(), $"Error: Skill '{skill}' not found.", StringComparison.Ordinal);
        }

        // A new object, because the old one wraps the model's own call record, which is stored and sent back.
        // It shares the old Context: that is where the turn is filed and where the wrappers leave their marks.
        private static AIFunctionArguments Replaced(AIFunctionArguments arguments, Dictionary<string, object?> replacement)
        {
            return new AIFunctionArguments(replacement)
            {
                Context = arguments.Context,
                Services = arguments.Services,
            };
        }
    }
}
