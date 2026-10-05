using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Security.Authentication;
using AgentCore.Application.Runtime.Turn;
using AgentCore.Application.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using AgentCore.Application.Runtime.Cut;
using AgentCore.Application.Runtime.Session;

namespace AgentCore.Application.Runtime.ToolCalls
{
    /// <summary>
    /// The function-invocation loop of <c>Microsoft.Extensions.AI</c>, with the tool error policy applied to every
    /// call this loop makes: a fault the model can answer becomes the error result it reads, and a fault beyond the
    /// model is marked with its call id and rethrown. <see cref="Hooks.Layers.ToolHookMiddleware"/> reports each call.
    /// </summary>
    internal sealed class AuditingFunctionInvokingChatClient : FunctionInvokingChatClient
    {
        private readonly ConcurrentDictionary<string, Drain> _drains = new(StringComparer.Ordinal);

        private sealed record Drain(IReadOnlyList<ITurnAttachments> Attachments, bool Nested, TurnCutSlot? Turn, ConversationToolRuns? Runs)
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
        /// Runs one tool, turns a fault the model can answer into the error result it reads, and marks and
        /// rethrows a fault beyond the model.
        /// </summary>
        protected override async ValueTask<object?> InvokeFunctionAsync(
            FunctionInvocationContext context,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(context);

            string callId = context.CallContent.CallId;

            TurnInvocation? invocation = null;
            bool nested = false;

            if (context.Options?.AdditionalProperties?.TryGetValue(TurnInvocation.ArgumentsKey, out object? top) == true
                && top is TurnInvocation own)
            {
                invocation = own with { OuterCallId = own.OuterCallId ?? callId };
                nested = own.Nested;
            }

            Drain? drain = null;
            if (invocation is not null)
            {
                // Snapshot the turn once per tool call and file it beside the call's arguments, so tools
                // declare what they need as parameters.
                _ = invocation.FileIn(context.Arguments);
                drain = new Drain(invocation.Attachments(), nested, invocation.CutSlot, invocation.ToolRuns);
                _drains[callId] = drain;
                invocation.Results?.NoteCall(callId, Release);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return ToolErrorResult.Create(context.Function.Name, "the turn was stopped before this call ran.");
            }

            // A background child's run carries no turn on its options; its session names the turn that started it.
            TurnInvocation? owner = invocation ?? TurnRegistry.For(AIAgent.CurrentRunContext?.Session);
            using CancellationTokenSource? linked = ToolToken(owner, cancellationToken, out CancellationToken toolToken);
            if (owner?.ToolRuns is not { } runs)
            {
                return await InvokeAndRecordAsync(context, invocation, toolToken).ConfigureAwait(false);
            }

            ToolRun run = runs.Start(context.CallContent, () => InvokeAndRecordAsync(context, invocation, toolToken).AsTask());
            if (invocation is not { Nested: false, CutSlot: { } turn })
            {
                return await run.Running.ConfigureAwait(false);
            }

            IEnumerable<AIContent> Attachments() => drain?.TakeFor(callId) ?? [];
            object? result;
            try
            {
                result = await run.Running.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (runs.Carry(run, Attachments, turn))
                {
                    throw;
                }

                result = await run.Running.ConfigureAwait(false);
            }

            runs.Returned(run, result, Attachments, turn);
            return result;
        }

        private async ValueTask<object?> InvokeAndRecordAsync(FunctionInvocationContext context, TurnInvocation? invocation, CancellationToken toolToken)
        {
            try
            {
                object? result = await base.InvokeFunctionAsync(context, toolToken).ConfigureAwait(false);

                if (invocation is { Nested: false })
                {
                    invocation.Results?.Record(context.Function.Name, result);
                }

                return result;
            }
            catch (Exception failure) when (!IsBeyondTheModel(failure))
            {
                return ToolErrorResult.Create(context.Function.Name, failure.GetType().Name + ": " + failure.Message);
            }
            catch (Exception failure)
            {
                ToolFaultMark.Put(failure, context.CallContent.CallId);

                throw;
            }
        }

        /// <summary>
        /// Picks the token one tool call runs under. A conversation turn's call reads only the end backstop: the turn's
        /// own token is cancelled by every cut and withdraw. Any other call reads its caller's token,
        /// linked to the end backstop of the conversation it runs for, if any, such as a <c>background:</c> child's.
        /// </summary>
        /// <returns>The linked source the caller disposes after the call, or <see langword="null"/> when none was made.</returns>
        private static CancellationTokenSource? ToolToken(TurnInvocation? owner, CancellationToken caller, out CancellationToken toolToken)
        {
            if (owner is { CutSlot: not null })
            {
                toolToken = owner.ToolStop;
                return null;
            }

            CancellationToken backstop = owner?.ToolStop ?? CancellationToken.None;
            if (!backstop.CanBeCanceled || backstop == caller)
            {
                toolToken = caller;
                return null;
            }

            if (!caller.CanBeCanceled)
            {
                toolToken = backstop;
                return null;
            }

            CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(caller, backstop);
            toolToken = linked.Token;
            return linked;
        }

        /// <summary>Reports whether a fault is one the model cannot possibly answer.</summary>
        internal static bool IsBeyondTheModel(Exception failure)
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

                // A turn's running tool is never handed the turn's token, so this is a deadline, the end backstop, or the
                // cancel of a run outside a turn. HttpClient reports its own deadline that way: a TaskCanceledException
                // wrapping a TimeoutException.
                OperationCanceledException => true,

                _ => false,
            };
        }

        /// <summary>
        /// Builds the messages the model reads, and attaches whatever this turn cited or published to the
        /// tool-result message it belongs to.
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
                if (!_drains.TryRemove(callId, out Drain? drain))
                {
                    continue;
                }

                if (drain is { Turn: { } turn, Runs: { } runs })
                {
                    runs.Delivered(turn, callId);
                }

                if (!drain.Nested)
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
