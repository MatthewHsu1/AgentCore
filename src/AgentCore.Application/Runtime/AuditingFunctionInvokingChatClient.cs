using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Security.Authentication;
using AgentCore.Application.Runtime.Turn;
using AgentCore.Application.Tools;
using AgentCore.Domain.Audit;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Runtime
{
    /// <summary>One tool call that did not run to completion, as the function-invocation loop saw it.</summary>
    internal sealed record ToolFailure
    {
        /// <summary>Gets the name the MODEL called.</summary>

        public required string ToolName { get; init; }

        /// <summary>Gets the id the model gave this one call.</summary>
        public required string ToolCallId { get; init; }

        /// <summary>Gets which of the two ways a tool call fails this one was.</summary>
        public required ToolFailureKind Kind { get; init; }

        /// <summary>Gets what went wrong, in one sentence. It never holds a secret value.</summary>
        public required string Message { get; init; }
    }

    /// <summary>
    /// The function-invocation loop of <c>Microsoft.Extensions.AI</c>, with every tool call that did not
    /// run to completion reported to the call that made it, and the tool error policy applied to every
    /// call this loop makes.
    /// </summary>
    internal sealed class AuditingFunctionInvokingChatClient : FunctionInvokingChatClient
    {
        // Drain state per open tool call, keyed by the model's own call id. The round that ends a
        // turn never reaches CreateResponseMessages, so a final round's entries are swept at turn
        // end through Release; every other entry is consumed where it drains.
        private readonly ConcurrentDictionary<string, Drain> _drains = new(StringComparer.Ordinal);

        private sealed record Drain(
            IReadOnlyList<ITurnAttachments> Attachments,
            Action<ToolFailure>? OnToolFailure,
            bool Nested)
        {
            /// <summary>Takes what one call produced, kind by kind, in the turn's own order.</summary>
            public IEnumerable<AIContent> TakeFor(string callId)
            {
                return Attachments.SelectMany(kind => kind.TakeFor(callId));
            }
        }

        /// <summary>Creates the client.</summary>
        /// <param name="innerClient">The model this loop sends its rounds to.</param>
        internal AuditingFunctionInvokingChatClient(IChatClient innerClient)
            : base(innerClient)
        {
        }

        /// <summary>Drops drain state for calls whose round never drained, at turn end.</summary>
        /// <param name="callIds">Every tool call id the turn recorded.</param>
        internal void Release(IReadOnlyList<string> callIds)
        {
            ArgumentNullException.ThrowIfNull(callIds);

            foreach (string callId in callIds)
            {
                _ = _drains.TryRemove(callId, out _);
            }
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

            string callId = context.CallContent.CallId;

            // The turn arrives on the run's own options — filed there by the loop for the outer
            // run and by the delegating bridge for a nested one, one value per run, so concurrent
            // runs never share it. A call with no turn runs bare, exactly as the null branch did
            // before. Nested runs arrive already stripped per K42 with the outermost call id kept,
            // so nothing here recomputes either.
            TurnInvocation? invocation = null;
            bool nested = false;

            if (context.Options?.AdditionalProperties?.TryGetValue(TurnInvocation.ArgumentsKey, out object? top) == true
                && top is TurnInvocation own)
            {
                invocation = own with { OuterCallId = own.OuterCallId ?? callId };
                nested = own.Nested;
            }

            if (invocation is not null)
            {
                // Snapshot the turn once per tool call and file it beside the call's arguments, so tools
                // declare what they need as parameters.
                _ = invocation.FileIn(context.Arguments);
                _drains[callId] = new Drain(invocation.Attachments(), invocation.OnToolFailure, nested);
                invocation.Results?.NoteCall(callId, Release);
            }

            using OuterCall? outerCall = invocation is not null && !nested
                ? OuterCall.Open(_drains[callId].Attachments, invocation.OuterCallId ?? callId)
                : null;

            try
            {
                object? result = await base.InvokeFunctionAsync(context, cancellationToken).ConfigureAwait(false);

                if (!nested)
                {
                    invocation?.Results?.Record(context.Function.Name, result);
                }

                return result;
            }
            catch (Exception failure) when (!IsCallerCancellation(failure, cancellationToken)
                                            && !IsBeyondTheModel(failure))
            {
                return ToolErrorResult.Create(context.Function.Name, failure.GetType().Name + ": " + failure.Message);
            }
            catch (Exception failure) when (!IsCallerCancellation(failure, cancellationToken))
            {
                invocation?.OnToolFailure?.Invoke(new ToolFailure
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
        private static bool IsBeyondTheModel(Exception failure)
        {
            return failure switch
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
        }

        /// <summary>Reports whether a fault is the caller hanging up rather than the tool failing.</summary>
        private static bool IsCallerCancellation(Exception failure, CancellationToken cancellationToken)
        {
            return failure is OperationCanceledException && cancellationToken.IsCancellationRequested;
        }

        /// <summary>
        /// Builds the messages the model reads, reports every tool it could not find, and attaches
        /// whatever this turn cited or published to the tool-result message it belongs to.
        /// </summary>
        protected override IList<ChatMessage> CreateResponseMessages(ReadOnlySpan<FunctionInvocationResult> results)
        {
            IList<ChatMessage> messages = base.CreateResponseMessages(results);

            foreach (ChatMessage message in messages)
            {
                Attach(message.Contents);
            }

            return messages;
        }

        /// <summary>Appends what each tool result in one message cited or published.</summary>
        private void Attach(IList<AIContent> contents)
        {
            foreach (string? callId in contents.OfType<FunctionResultContent>().Select(r => r.CallId).ToList())
            {
                if (_drains.TryRemove(callId, out Drain? drain) && !drain.Nested)
                {
                    foreach (AIContent attached in drain.TakeFor(callId))
                    {
                        contents.Add(attached);
                    }
                }
            }
        }
    }
}
