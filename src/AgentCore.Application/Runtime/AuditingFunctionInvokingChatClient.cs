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
    protected override async ValueTask<object?> InvokeFunctionAsync(
        FunctionInvocationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        using var outerCall = OuterToolCall.Enter(context.CallContent.CallId);

        try
        {
            return await base.InvokeFunctionAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (!IsCallerCancellation(failure, cancellationToken)
                                        && !IsBeyondTheModel(failure))
        {
            return ToolErrorResult.Create(context.Function.Name, failure.GetType().Name + ": " + failure.Message);
        }
        catch (Exception failure) when (!IsCallerCancellation(failure, cancellationToken))
        {
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
    private static bool IsCallerCancellation(Exception failure, CancellationToken cancellationToken)
        => failure is OperationCanceledException && cancellationToken.IsCancellationRequested;

    /// <summary>
    /// Builds the messages the model reads, reports every tool it could not find, and attaches
    /// whatever this turn drew or cited to the tool-result message it belongs to.
    /// </summary>
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

        var messages = base.CreateResponseMessages(results);

        // A nested loop's own InvokeFunctionAsync opens a no-op OuterToolCall scope (outermost
        // wins), so the outer scope is still open for every round the nested loop runs, and only
        // closes once the outer call that started it returns. A non-null Current here therefore
        // means this round belongs to that nested loop, not to the call the caller's own transcript
        // holds: draining here risks a nested tool call whose id happens to match the outer one,
        // which would attach the drawing to a message that never reaches the caller. Only the round
        // that runs with no OuterToolCall scope left open — the outermost loop's own — may drain.
        if (OuterToolCall.Current is not null)
        {
            return messages;
        }

        var renders = TurnAmbients.Current?.Renders;
        var sources = TurnAmbients.Current?.Sources;

        if (renders is null && sources is null)
        {
            return messages;
        }

        foreach (var message in messages)
        {
            // Materialised before the loop below adds to the very list this reads: Contents is a
            // List<AIContent> underneath, and its enumerator throws on the next MoveNext once
            // anything has been appended, even where nothing further was left to enumerate.
            foreach (var callId in message.Contents.OfType<FunctionResultContent>().Select(r => r.CallId).ToList())
            {
                foreach (var drawn in renders?.TakeFor(callId) ?? [])
                {
                    message.Contents.Add(drawn);
                }

                foreach (var cited in sources?.TakeFor(callId) ?? [])
                {
                    message.Contents.Add(cited);
                }
            }
        }

        return messages;
    }
}
