using AgentCore.Domain.Audit;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Runtime;

/// <summary>
/// The function-invocation loop of <c>Microsoft.Extensions.AI</c>, with every tool call that did not
/// run to completion reported to the call that made it.
/// </summary>
/// <remarks>
/// <para>
/// <b>It observes and it never changes anything.</b> Every override calls its base and returns what
/// the base returned, unchanged, so the model reads exactly the messages it read before, the error
/// budget counts exactly what it counted before, and the fault that ends a turn is the same instance
/// with the same stack. A record of the call must never be a part of the call.
/// </para>
/// <para>
/// <b>Why a subclass and not a wrapper.</b> The framework already knows which tool failed and why, and
/// then throws that knowledge away: <c>MaximumConsecutiveErrorsPerRequest</c> rethrows the original
/// exception through <c>ExceptionDispatchInfo</c> and carries NO function name with it, and
/// <c>IncludeDetailedErrors</c> is false by default so even the model is told only
/// <c>"Error: Function failed."</c>. <see cref="CallSession"/> therefore catches a fault it cannot
/// attribute, which is why <c>tool.failed</c> has never named a tool. Nothing outside this loop can
/// recover the name, so the seam has to be inside it.
/// </para>
/// <para>
/// <b>Why TWO overrides, and why each one takes only half the work.</b> They are not
/// interchangeable and neither is sufficient alone.
/// </para>
/// <list type="bullet">
/// <item><description>
/// <see cref="CreateResponseMessages"/> is the only hook that sees a <c>NotFound</c> result — a name
/// the MODEL invented. The framework never invokes a tool it could not find, so the invocation hook
/// below is never reached for one, and <c>NotFound</c> carries no exception, spends none of the error
/// budget, and resets it to zero. It is the failure nothing in this system has ever recorded.
/// </description></item>
/// <item><description>
/// <see cref="InvokeFunctionAsync"/> is the only hook that sees the fault that ENDS the turn.
/// Measured against Microsoft.Extensions.AI 10.8.3: <c>ProcessFunctionCallsAsync</c> computes
/// <c>captureExceptionsWhenSerial = consecutiveErrorCount &lt; MaximumConsecutiveErrorsPerRequest</c>,
/// so on the fourth consecutive erroring round the exception is NOT captured — it leaves
/// <c>ProcessSingleFunctionCallAsync</c> directly and <see cref="CreateResponseMessages"/> is never
/// called for that round at all. Recording exceptions only in <see cref="CreateResponseMessages"/>
/// would therefore name the three failures that did not matter and lose the one that did.
/// </description></item>
/// </list>
/// <para>
/// The split is exact, so nothing is recorded twice: an exception is recorded where it is thrown, and
/// <c>NotFound</c> — which is never thrown — is recorded where the result is turned into a message.
/// </para>
/// <para>
/// It holds nothing per call. T44 makes the compiled agent a process singleton and this client is
/// built with it, so the call it reports to is found through <see cref="ToolFailureScope"/>, on the
/// flow of execution the turn runs on.
/// </para>
/// </remarks>
internal sealed class AuditingFunctionInvokingChatClient : FunctionInvokingChatClient
{
    /// <summary>Creates the client.</summary>
    /// <param name="innerClient">The model this loop sends its rounds to.</param>
    internal AuditingFunctionInvokingChatClient(IChatClient innerClient)
        : base(innerClient)
    {
    }

    /// <summary>Runs one tool, and reports it when it throws.</summary>
    /// <param name="context">Everything the framework knows about this one call.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Whatever the tool returned.</returns>
    /// <remarks>
    /// The fault is reported and then rethrown untouched, so the budget still counts it and the turn
    /// still ends the way section 8.7 row six says it does.
    /// </remarks>
    protected override async ValueTask<object?> InvokeFunctionAsync(
        FunctionInvocationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            return await base.InvokeFunctionAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            // The TOKEN decides whether this was a failure at all, which is the rule the framework
            // itself uses one frame below: a cancelled token means the caller hung up, nobody reads
            // the result, and the call is over. Anything else — including a deadline that arrives
            // spelled as a TaskCanceledException — is a tool that failed.
            if (!cancellationToken.IsCancellationRequested)
            {
                ToolFailureScope.Report(new ToolFailure
                {
                    ToolName = context.CallContent.Name,
                    ToolCallId = context.CallContent.CallId,
                    Kind = ToolFailureKind.Faulted,
                    Message = failure.GetType().Name + ": " + failure.Message,
                });
            }

            throw;
        }
    }

    /// <summary>Builds the messages the model reads, and reports every tool it could not find.</summary>
    /// <param name="results">What each call of this round did.</param>
    /// <returns>Exactly what the base built, unchanged.</returns>
    protected override IList<ChatMessage> CreateResponseMessages(ReadOnlySpan<FunctionInvocationResult> results)
    {
        foreach (FunctionInvocationResult result in results)
        {
            // Only NotFound. An exception was already reported where it was thrown, and the round
            // that ends the turn never reaches this method at all. See the remarks on this class.
            if (result.Status != FunctionInvocationStatus.NotFound)
            {
                continue;
            }

            ToolFailureScope.Report(new ToolFailure
            {
                ToolName = result.CallContent.Name,
                ToolCallId = result.CallContent.CallId,
                Kind = ToolFailureKind.Undeclared,
                Message = $"the model called '{result.CallContent.Name}', and the document declares no such tool.",
            });
        }

        return base.CreateResponseMessages(results);
    }
}
