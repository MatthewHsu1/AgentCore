using System.Net.Sockets;
using System.Security.Authentication;
using AgentCore.Application.Tools;
using AgentCore.Domain.Audit;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Runtime;

/// <summary>
/// The function-invocation loop of <c>Microsoft.Extensions.AI</c>, with every tool call that did not
/// run to completion reported to the call that made it, and the tool error policy applied to every
/// call this loop makes.
/// </summary>
/// <remarks>
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
/// <para>
/// <b>Task 7a: the error policy, moved here.</b> Section 8.7 says a tool returns an error result
/// rather than throwing, so the model still reads an answer and decides what to say next — for the
/// failure it was written about, and not for every failure. A fault the model cannot possibly answer
/// (a dead socket, a rejected credential, an endpoint that never replies) must still propagate, so
/// <c>MaximumConsecutiveErrorsPerRequest</c> can end the turn on the fallback line per section 8.7 row
/// six. <see cref="DeclaredTool"/> used to draw that split itself, which meant only a
/// <see cref="DeclaredTool"/> got it: a plain <c>AIFunctionFactory.Create(...)</c> tool got none of
/// this. <see cref="InvokeFunctionAsync"/> is the framework's single choke point for every tool call
/// regardless of kind, so the split now lives here instead, and every tool gets identical treatment.
/// </para>
/// <para>
/// <b>Classify before reporting.</b> Before this move, a fault the model could answer never left a
/// <see cref="DeclaredTool"/> as an exception at all — it came back as a result — so
/// <see cref="ToolFailureScope.Report"/> never fired for it; only a propagating fault reached this
/// method and was reported. Now both kinds reach here, so the classification below runs FIRST, and
/// only the fault that goes on to propagate is reported. Reporting an answerable fault too would
/// silently change what reaches the audit chain.
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

    /// <summary>
    /// Runs one tool, turns a fault the model can answer into the error result it reads, and reports
    /// and rethrows a fault beyond the model.
    /// </summary>
    /// <param name="context">Everything the framework knows about this one call.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>Whatever the tool returned, or the error result.</returns>
    /// <remarks>
    /// <para>
    /// Both catches are exception FILTERS, so a fault that belongs to neither — the caller's own
    /// cancellation — is never caught at all: it keeps the stack it was thrown with, and the framework
    /// rethrows that same instance through <c>ExceptionDispatchInfo</c> when the budget runs out. A
    /// <c>catch</c> that rethrew would give the log a stack that starts here instead of at the socket.
    /// </para>
    /// <para>
    /// The two catches must stay in this order. The first classifies an answerable fault and returns
    /// without reporting it — see the remarks on this class for why reporting only the propagating
    /// fault matters. The second is reached only when the first's filter was false, which is exactly
    /// the fault beyond the model; it reports and then rethrows with a bare <c>throw;</c>, which is
    /// what preserves the stack for the fault that ends the turn.
    /// </para>
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
        catch (Exception failure) when (!IsCallerCancellation(failure, cancellationToken)
                                        && !IsBeyondTheModel(failure))
        {
            // A fault the model can answer never leaves this method as an exception, so it must never
            // reach ToolFailureScope.Report — only a propagating fault is reported. See the remarks on
            // this class: that was already true before this method classified anything, because a
            // DeclaredTool never let an answerable fault out as an exception in the first place.
            return ToolErrorResult.Create(context.Function.Name, failure.GetType().Name + ": " + failure.Message);
        }
        catch (Exception failure) when (!IsCallerCancellation(failure, cancellationToken))
        {
            // The TOKEN decides whether this was a failure at all, which is the rule the framework
            // itself uses one frame below: a cancelled token means the caller hung up, nobody reads
            // the result, and the call is over. Anything else — including a deadline that arrives
            // spelled as a TaskCanceledException — is a tool that failed.
            ToolFailureScope.Report(new ToolFailure
            {
                ToolName = context.CallContent.Name,
                ToolCallId = context.CallContent.CallId,
                Kind = ToolFailureKind.Faulted,
                Message = failure.GetType().Name + ": " + failure.Message,
            });

            throw;
        }
    }

    /// <summary>Reports whether a fault is one the model cannot possibly answer.</summary>
    /// <param name="failure">What the tool threw. It is never the caller's own cancellation.</param>
    /// <returns>
    /// <see langword="true"/> to let the fault propagate, so the framework's consecutive-error budget
    /// counts it and eventually ends the turn on the fallback line.
    /// <see langword="false"/> to answer the model with the error result <see cref="ToolErrorResult"/>
    /// builds.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The exception type is the only signal available here, because the body of a tool is arbitrary
    /// and nothing else about it is known. The set below is therefore deliberately SMALL and each
    /// member earns its place by naming a dependency that is not there — not a request the dependency
    /// refused, which is a fact the model reads and works around.
    /// </para>
    /// <para>
    /// Everything unlisted answers the model. That default is chosen and not accidental: an unknown
    /// fault from an arbitrary tool body is far more often a bad argument than a dead dependency, and
    /// the two mistakes do not cost the same. Guessing wrong here wastes one round of the model's
    /// budget. Guessing wrong the other way ends the caller's turn on the fallback line for something
    /// the model would have fixed by itself.
    /// </para>
    /// <para>
    /// <b>This used to be <c>protected virtual</c> on <see cref="DeclaredTool"/>, so a tool kind could
    /// refine it for a vendor SDK exception type not visible from the Application assembly.</b> Task
    /// 7a collapsed that seam: nothing shipped in this repository ever overrode it — only a test did —
    /// and <c>PublicAPI.Shipped.txt</c> is still empty, so nothing outside this assembly could have
    /// depended on the override point either. D15 makes an unused public member a permanent obligation
    /// the moment it ships; leaving a virtual method with no real caller in place until then, rather
    /// than closing it off now while it costs nothing, would have been the same coupling this move
    /// exists to remove, just moved one method over. If a tool kind with a vendor-specific exception
    /// type is ever added, the seam is one method to reopen, and it should be reopened then, against a
    /// real caller, and not kept warm against a hypothetical one.
    /// </para>
    /// </remarks>
    private static bool IsBeyondTheModel(Exception failure) => failure switch
    {
        // A path the model named that is not there IS answerable: it picks another one. These two
        // come first, because both derive from IOException and the arm below would otherwise swallow
        // them. A knowledge tool that reads a document the model chose lands here.
        FileNotFoundException or DirectoryNotFoundException => false,

        // The host is not resolvable, the connection was refused, or it dropped mid-body. HttpTool
        // answers a status code with Failed() itself, so this type only ever reaches here as
        // transport. No set of arguments reaches a host that is not answering.
        HttpRequestException or SocketException => true,

        // A pipe, a socket, or a file handle that faulted below the tool. The two "not there" cases
        // were already taken above, so what is left is the medium and not the name.
        IOException => true,

        // Nothing answered inside the deadline. A second attempt with different arguments waits the
        // same amount of time and the caller is on the telephone.
        TimeoutException => true,

        // The credential was refused, or the process may not read what it was told to read. Neither
        // is a fact the model holds, and retrying a rejected token only rate-limits us.
        UnauthorizedAccessException or AuthenticationException => true,

        // The caller's own cancellation never reaches here — the first catch filter tests the token
        // first — so what is left is somebody else's deadline. HttpClient reports its own that way: a
        // TaskCanceledException wrapping a TimeoutException, on a token nobody cancelled.
        OperationCanceledException => true,

        _ => false,
    };

    /// <summary>Reports whether a fault is the caller hanging up rather than the tool failing.</summary>
    /// <remarks>
    /// The TOKEN decides this and never the type, because the two are spelled the same. A caller who
    /// hung up and an endpoint that ran out of time both arrive as an
    /// <see cref="OperationCanceledException"/>, and only the token says which happened.
    /// </remarks>
    private static bool IsCallerCancellation(Exception failure, CancellationToken cancellationToken)
        => failure is OperationCanceledException && cancellationToken.IsCancellationRequested;

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
                Message = $"the model called '{result.CallContent.Name}', and no such tool is declared.",
            });
        }

        return base.CreateResponseMessages(results);
    }
}
