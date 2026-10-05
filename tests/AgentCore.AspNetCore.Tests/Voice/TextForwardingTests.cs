using AgentCore.AspNetCore.Tests.Fakes;
using AgentCore.AspNetCore.Voice.Speech;
using AgentCore.AspNetCore.Voice.Speech.Replies;
using AgentCore.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Voice
{
    /// <summary>The ordering <see cref="TextForwarding.ForwardAsync"/> promises its caller.</summary>
    public sealed class TextForwardingTests
    {
        private readonly FakeTimeProvider _time = new(DateTimeOffset.UnixEpoch);

        // agent_activity.py:3257 registers _on_first_frame on first_text_fut; asyncio runs that callback
        // before the forwarding task's completion wakes _tts_task_impl, so the "speaking" check at
        // agent_activity.py:3346 always sees it. The slow callback only makes a late one lose every time.
        [Fact(Timeout = 30_000)]
        public async Task OnFirstText_HasFinishedBeforeForwardingReturns()
        {
            FakeTimeProvider time = new(DateTimeOffset.UnixEpoch);
            SpeechHandle handle = SpeechHandle.Create(time, NullLogger.Instance);
            bool firstTextHandled = false;

            _ = await TextForwarding.ForwardAsync(
                handle,
                new FakeConversationOutput(),
                Fragment("hello"),
                onFirstText: () =>
                {
                    Thread.Sleep(TimeSpan.FromMilliseconds(200));
                    firstTextHandled = true;
                },
                handle.TaskCancellationToken);

            Assert.True(firstTextHandled);
        }

        // generation.py _text_forwarding_task: a delta read after the interruption is not forwarded.
        [Fact(Timeout = 30_000)]
        public async Task AFragmentReadAfterTheInterruption_IsNotForwarded_AndPlaybackIsPartial()
        {
            SpeechHandle handle = SpeechHandle.Create(_time, NullLogger.Instance);
            FakeConversationOutput output = new();

            TextForwardingResult result = await TextForwarding.ForwardAsync(
                handle, output, InterruptedBetween(handle, "Once", " upon"), onFirstText: null, handle.TaskCancellationToken);

            Assert.Equal(new TextForwardingResult("Once", TextPlayback.Partial), result);
            Assert.Equal(["Once"], output.Spoken);
        }

        [Fact(Timeout = 30_000)]
        public async Task NothingForwarded_IsSkipped()
        {
            SpeechHandle handle = SpeechHandle.Create(_time, NullLogger.Instance);

            TextForwardingResult result = await TextForwarding.ForwardAsync(
                handle, new FakeConversationOutput(), Fragments(), onFirstText: null, handle.TaskCancellationToken);

            Assert.Equal(new TextForwardingResult(string.Empty, TextPlayback.Skipped), result);
        }

        [Fact(Timeout = 30_000)]
        public async Task AnEmptyFragment_CountsAsFirstText_ButNeverReachesTheOutput()
        {
            SpeechHandle handle = SpeechHandle.Create(_time, NullLogger.Instance);
            FakeConversationOutput output = new();
            int firstTextCalls = 0;

            TextForwardingResult result = await TextForwarding.ForwardAsync(
                handle, output, Fragments(string.Empty, "hello", " there"), () => firstTextCalls++, handle.TaskCancellationToken);

            Assert.Equal(1, firstTextCalls);
            Assert.Equal(["hello", " there"], output.Spoken);
            Assert.Equal(new TextForwardingResult("hello there", TextPlayback.Full), result);
        }

        private static async IAsyncEnumerable<string> Fragment(string text)
        {
            yield return text;
        }

        private static async IAsyncEnumerable<string> Fragments(params string[] texts)
        {
            foreach (string text in texts)
            {
                yield return text;
            }
        }

        private static async IAsyncEnumerable<string> InterruptedBetween(SpeechHandle handle, string first, string second)
        {
            yield return first;
            _ = handle.Interrupt();
            yield return second;
        }
    }
}
