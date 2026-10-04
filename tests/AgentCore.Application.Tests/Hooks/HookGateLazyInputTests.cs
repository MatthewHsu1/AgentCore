using AgentCore.Application.Hooks;
using AgentCore.Application.Hooks.Gates;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Hooks
{
    public sealed class HookGateLazyInputTests
    {
        private static readonly HookScope Scope = new("c1", "main", 0, "", Guid.CreateVersion7(), 0, DateTimeOffset.UnixEpoch);

        private static RunGate Run() => new(Scope, "agent", nested: false, new Dictionary<string, object?>());

        // The runner seals a view at the deadline even while an abandoned hook is still inside a verb. The
        // verb stays blocked until the seal returned, so a seal that waited for it would only end at the bound.
        [Fact(Timeout = 30_000)]
        public async Task SealingDoesNotWaitForAHookBlockedInsideALazyInput()
        {
            CancellationToken token = TestContext.Current.CancellationToken;
            RunGate gate = Run();
            TaskCompletionSource enumerating = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using ManualResetEventSlim release = new();
            Task verb = Task.Run(() => gate.AddMessages(Blocking(enumerating, release, token)), token);
            await enumerating.Task.WaitAsync(token);

            Task sealing = Task.Run(gate.Seal, token);
            Exception? sealFailure = await Record.ExceptionAsync(() => sealing.WaitAsync(TimeSpan.FromSeconds(20), token));
            release.Set();
            _ = await Assert.ThrowsAsync<InvalidOperationException>(() => verb);

            Assert.Null(sealFailure);
            Assert.Empty(gate.Messages);
        }

        // A verb is applied whole or not at all.
        [Fact]
        public void AnInputThatThrowsMidwayStagesNothing()
        {
            RunGate gate = Run();

            _ = Assert.Throws<FormatException>(() => gate.AddMessages(ThrowingAfterOne()));

            Assert.Empty(gate.Messages);
            Assert.False(gate.IsTerminal);
        }

        private static IEnumerable<ChatMessage> Blocking(TaskCompletionSource enumerating, ManualResetEventSlim release, CancellationToken token)
        {
            yield return new ChatMessage(ChatRole.User, "first");
            _ = enumerating.TrySetResult();
            release.Wait(token);
            yield return new ChatMessage(ChatRole.User, "second");
        }

        private static IEnumerable<ChatMessage> ThrowingAfterOne()
        {
            yield return new ChatMessage(ChatRole.User, "first");
            throw new FormatException("bad input");
        }
    }
}
