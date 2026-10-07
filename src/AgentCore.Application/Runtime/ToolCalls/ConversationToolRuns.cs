using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using AgentCore.Application.Runtime.Cut;

namespace AgentCore.Application.Runtime.ToolCalls
{
    /// <summary>
    /// The tool calls running on one conversation. A cut never waits for a started call: the call
    /// runs on, and once it has a result, its call and result go to <paramref name="keep"/>, which stores them in the
    /// conversation's history, so no later turn runs the tool again. The calls of its round that already returned their
    /// results are kept with it (<see cref="ReturnedToolCalls"/>). A call that ends with no result, as one the end
    /// backstop stops, leaves nothing to keep.
    /// </summary>
    /// <param name="keep">Stores the call and the result of a finished call its turn no longer waited for.</param>
    internal sealed class ConversationToolRuns(Action<IReadOnlyList<ChatMessage>> keep)
    {
        private readonly Lock _gate = new();

        private int _running;

        private TaskCompletionSource? _idle;

        private int _carried;

        private TaskCompletionSource? _carriedDone;

        private readonly ConditionalWeakTable<TurnCutSlot, ReturnedToolCalls> _returned = [];

        /// <summary>Gets whether no tool call of this conversation is running.</summary>
        internal bool IsIdle
        {
            get
            {
                lock (_gate)
                {
                    return _running == 0;
                }
            }
        }

        /// <summary>Starts one tool call and counts it until it ends.</summary>
        /// <param name="call">The call the model made.</param>
        /// <param name="invoke">Runs the call.</param>
        /// <returns>The running call.</returns>
        internal ToolRun Start(FunctionCallContent call, Func<Task<object?>> invoke)
        {
            ArgumentNullException.ThrowIfNull(call);
            ArgumentNullException.ThrowIfNull(invoke);

            ToolRun run = new(call);
            lock (_gate)
            {
                _running++;
            }

            run.Running = WatchAsync(run, invoke);
            return run;
        }

        /// <summary>
        /// Lets a call its turn no longer waits for run on: its call and result are kept once it finished, and the calls
        /// of its round that already returned are kept now.
        /// </summary>
        /// <param name="run">The call.</param>
        /// <param name="attachments">Reads what the call cited or published, once it finished.</param>
        /// <param name="turn">The turn that made the call.</param>
        /// <returns><see langword="false"/> when the call already finished, so its turn takes the result itself.</returns>
        internal bool Carry(ToolRun run, Func<IEnumerable<AIContent>> attachments, TurnCutSlot turn)
        {
            ArgumentNullException.ThrowIfNull(run);
            ArgumentNullException.ThrowIfNull(attachments);
            ArgumentNullException.ThrowIfNull(turn);

            List<Func<IReadOnlyList<ChatMessage>>> siblings;
            lock (_gate)
            {
                if (run.Done)
                {
                    return false;
                }

                run.Attachments = attachments;
                _carried++;
                siblings = _returned.GetOrCreateValue(turn).Break();
            }

            foreach (Func<IReadOnlyList<ChatMessage>> sibling in siblings)
            {
                keep(sibling());
            }

            // Nothing awaits a carried call, so its fault is observed here; the tool hook already reported it.
            _ = run.Running.ContinueWith(
                static task => task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return true;
        }

        /// <summary>
        /// Notes a call whose result went back to its turn: kept now when another call of the turn was carried, else held
        /// until a response message carries it (<see cref="Delivered"/>).
        /// </summary>
        /// <param name="run">The call.</param>
        /// <param name="result">What the call returned.</param>
        /// <param name="attachments">Reads what the call cited or published.</param>
        /// <param name="turn">The turn that made the call.</param>
        internal void Returned(ToolRun run, object? result, Func<IEnumerable<AIContent>> attachments, TurnCutSlot turn)
        {
            ArgumentNullException.ThrowIfNull(run);
            ArgumentNullException.ThrowIfNull(attachments);
            ArgumentNullException.ThrowIfNull(turn);

            IReadOnlyList<ChatMessage> Pair()
            {
                return PairOf(run.Call, result, attachments());
            }

            bool now;
            lock (_gate)
            {
                now = _returned.GetOrCreateValue(turn).Add(run.Call.CallId, Pair);
            }

            if (now)
            {
                keep(Pair());
            }
        }

        /// <summary>Forgets a returned call once a response message of its turn carries its result.</summary>
        /// <param name="turn">The turn that made the call.</param>
        /// <param name="callId">The call.</param>
        internal void Delivered(TurnCutSlot turn, string callId)
        {
            lock (_gate)
            {
                if (_returned.TryGetValue(turn, out ReturnedToolCalls? returned))
                {
                    returned.Delivered(callId);
                }
            }
        }

        /// <summary>Completes once no tool call of this conversation is running.</summary>
        internal Task WhenIdle()
        {
            lock (_gate)
            {
                return _running == 0
                    ? Task.CompletedTask
                    : (_idle ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            }
        }

        /// <summary>
        /// Completes once no call that <see cref="Carry"/> let run on is still running. By then each one's call and result
        /// was kept, so a turn that starts after it reads them.
        /// </summary>
        internal Task WhenCarriedDone()
        {
            lock (_gate)
            {
                return _carried == 0
                    ? Task.CompletedTask
                    : (_carriedDone ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            }
        }

        // The pair is kept before the call is counted out, so a caller that saw the conversation idle finds it stored.
        private async Task<object?> WatchAsync(ToolRun run, Func<Task<object?>> invoke)
        {
            object? result = null;
            bool answered = false;
            try
            {
                result = await invoke().ConfigureAwait(false);
                answered = true;
                return result;
            }
            finally
            {
                Func<IEnumerable<AIContent>>? carried;
                lock (_gate)
                {
                    run.Done = true;
                    carried = run.Attachments;
                }

                try
                {
                    if (answered && carried is not null)
                    {
                        keep(PairOf(run.Call, result, carried()));
                    }
                }
                finally
                {
                    if (carried is not null)
                    {
                        CarriedOut();
                    }

                    CountOut();
                }
            }
        }

        private static IReadOnlyList<ChatMessage> PairOf(FunctionCallContent call, object? result, IEnumerable<AIContent> attachments)
        {
            return [
            new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(call.CallId, call.Name, call.Arguments)]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent(call.CallId, result), .. attachments]),
        ];
        }

        private void CarriedOut()
        {
            TaskCompletionSource? done = null;
            lock (_gate)
            {
                if (--_carried == 0)
                {
                    done = _carriedDone;
                    _carriedDone = null;
                }
            }

            _ = done?.TrySetResult();
        }

        private void CountOut()
        {
            TaskCompletionSource? idle = null;
            lock (_gate)
            {
                if (--_running == 0)
                {
                    idle = _idle;
                    _idle = null;
                }
            }

            _ = idle?.TrySetResult();
        }
    }
}
