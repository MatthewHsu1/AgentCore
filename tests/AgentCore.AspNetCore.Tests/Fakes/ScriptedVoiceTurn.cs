using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.AI;

namespace AgentCore.AspNetCore.Tests.Fakes
{
    /// <summary>One engine turn a test streams by hand, one update at a time.</summary>
    internal sealed class ScriptedVoiceTurn
    {
        private readonly Channel<ChatResponseUpdate> _updates = Channel.CreateUnbounded<ChatResponseUpdate>();
        private readonly Channel<bool> _taken = Channel.CreateUnbounded<bool>();
        private readonly TaskCompletionSource<string> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Gets a task that completes with the caller's words once the engine turn has started.</summary>
        public Task<string> Started => _started.Task;

        /// <summary>Gets a task that completes once the engine turn has ended, however it ended.</summary>
        public Task Finished => _finished.Task;

        /// <summary>Streams text, then waits until the reader has handled it.</summary>
        public Task TextAsync(string text) => PushAsync(new TextContent(text));

        /// <summary>Streams a tool call, then waits until the reader has handled it.</summary>
        public Task CallAsync(string callId, string name = "lookup") => PushAsync(new FunctionCallContent(callId, name));

        /// <summary>Streams a tool result, then waits until the reader has handled it.</summary>
        public Task ResultAsync(string callId) => PushAsync(new FunctionResultContent(callId, "done"));

        /// <summary>Ends the turn once every update pushed so far is read.</summary>
        public void End() => _updates.Writer.TryComplete();

        /// <summary>Plays the turn to the engine's reader.</summary>
        internal async IAsyncEnumerable<ChatResponseUpdate> StreamAsync(
            string userInput,
            Action onEnd,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            try
            {
                _ = _started.TrySetResult(userInput);
                while (await _updates.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    while (_updates.Reader.TryRead(out ChatResponseUpdate? update))
                    {
                        yield return update;
                        _ = _taken.Writer.TryWrite(true);
                    }
                }
            }
            finally
            {
                onEnd();
                _ = _finished.TrySetResult();
            }
        }

        private async Task PushAsync(AIContent content)
        {
            await _updates.Writer.WriteAsync(new ChatResponseUpdate(ChatRole.Assistant, [content])).ConfigureAwait(false);
            _ = await Task.WhenAny(_taken.Reader.ReadAsync().AsTask(), _finished.Task).ConfigureAwait(false);
        }
    }
}
