using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tests.Hooks;
using AgentCore.Domain.Audit;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Application.Runtime.Cut;
using AgentCore.Application.Runtime.Session;
using AgentCore.Application.Runtime.Turn.Lifecycle;

namespace AgentCore.Application.Tests.Runtime
{
    /// <summary>
    /// The workspace outlives the end while a tool still runs in it: it is deleted once the tool ends, or once the end
    /// backstop stopped it <see cref="ConversationEnding.ToolGrace"/> later.
    /// </summary>
    public sealed class ConversationWorkspaceToolGraceTests : IDisposable
    {
        private static readonly DateTimeOffset Start = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

        private readonly string _root = Path.Combine(Path.GetTempPath(), "agentcore-grace-" + Guid.NewGuid().ToString("N"));

        private readonly FakeTimeProvider _time = new(Start);

        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private string? _workspace;

        private bool _ignoresStop;

        private static CancellationToken Ct => TestContext.Current.CancellationToken;

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        [Fact(Timeout = 20_000)]
        public async Task AToolInItsGraceKeepsItsFolderAndTheFolderGoesOnceTheToolEnds()
        {
            ConversationSession session = Create();
            Task reading = await StartToolTurnAsync(session);

            _ = session.EndConversation(ConversationEndReason.CallerHungUp);
            await _time.WaitForTimersAsync(Start + ConversationEnding.ToolGrace, 1).WaitAsync(TimeSpan.FromSeconds(5), Ct);
            _time.Advance(ConversationEnding.ToolGrace - TimeSpan.FromSeconds(1));
            Assert.True(Directory.Exists(_workspace));

            _release.SetResult();
            await EndedAsync(reading);
            await session.Lifetime.Cleanup.Pending.WaitAsync(TimeSpan.FromSeconds(5), Ct);

            Assert.Contains(session.Transcript.SelectMany(message => message.Contents).OfType<FunctionResultContent>(), result => result.Result?.ToString() == "written");
            Assert.False(Directory.Exists(_workspace));
        }

        // The backstop bounds the wait even for a tool that ignores its token.
        [Fact(Timeout = 20_000)]
        public async Task AToolStillRunningWhenTheBackstopFiresLosesItsFolderThen()
        {
            _ignoresStop = true;
            ConversationSession session = Create();
            Task reading = await StartToolTurnAsync(session);

            _ = session.EndConversation(ConversationEndReason.CallerHungUp);
            await _time.WaitForTimersAsync(Start + ConversationEnding.ToolGrace, 1).WaitAsync(TimeSpan.FromSeconds(5), Ct);
            _time.Advance(ConversationEnding.ToolGrace - TimeSpan.FromSeconds(1));
            Assert.True(Directory.Exists(_workspace));

            _time.Advance(TimeSpan.FromSeconds(1));
            await session.Lifetime.Cleanup.Pending.WaitAsync(TimeSpan.FromSeconds(5), Ct);

            Assert.False(Directory.Exists(_workspace));
            _release.SetResult();
            await EndedAsync(reading);
        }

        // A cut turn ended at once and left its tool running: a dispose, after an end or not, waits for that tool.
        [Theory(Timeout = 20_000)]
        [InlineData(true)]
        [InlineData(false)]
        public async Task ADisposeAfterACutWaitsForTheCutTurnsToolBeforeItTearsDown(bool ended)
        {
            ConversationSession session = Create();
            TurnRun run = await session.StartTurnAsync(new ChatMessage(ChatRole.User, "Max speed of the F63?"), origin: null, Ct);
            Task reading = ReadAllAsync(run);
            await _entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            Assert.True(session.Cut(run.TurnIndex, new TurnCut(string.Empty, TimeSpan.Zero)));
            await EndedAsync(reading);

            if (ended)
            {
                _ = session.EndConversation(ConversationEndReason.CallerHungUp);
            }

            Task disposing = session.DisposeAsync().AsTask();
            await _time.WaitForTimersAsync(Start + ConversationEnding.ToolGrace, 1).WaitAsync(TimeSpan.FromSeconds(5), Ct);
            Assert.True(Directory.Exists(_workspace));
            Assert.False(disposing.IsCompleted);

            _release.SetResult();
            await disposing.WaitAsync(TimeSpan.FromSeconds(5), Ct);

            Assert.Equal(!ended, Directory.Exists(_workspace));
        }

        private ConversationSession Create()
        {
            CompiledAgent compiled = HookSessions.Compile(
                HookSessions.ToolAgentYaml,
                new NewestWordsToolChatClient("F63"),
                tools: declared => AIFunctionFactory.Create(WriteAsync, declared.Id, declared.Description),
                time: _time)["main"];
            ConversationSession session = new ConversationSessionFactory(
                compiled, new GuardEvaluator(compiled.Configuration.Guards), timeProvider: _time, workspaceRoot: _root).Create("conversation-1");
            _workspace = session.Workspace;
            return session;
        }

        private async Task<Task> StartToolTurnAsync(ConversationSession session)
        {
            TurnRun run = await session.StartTurnAsync(new ChatMessage(ChatRole.User, "Max speed of the F63?"), origin: null, Ct);
            Task reading = ReadAllAsync(run);
            await _entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            return reading;
        }

        private async Task<string> WriteAsync(CancellationToken cancellationToken)
        {
            _ = _entered.TrySetResult();
            await _release.Task.WaitAsync(_ignoresStop ? CancellationToken.None : cancellationToken);

            if (!_ignoresStop)
            {
                await File.WriteAllTextAsync(Path.Combine(_workspace!, "out.txt"), "x", cancellationToken);
            }

            return "written";
        }

        private static Task ReadAllAsync(TurnRun run)
        {
            return Task.Run(
                async () =>
                {
                    await using (run)
                    {
                        await foreach (ChatResponseUpdate _ in run.Updates.WithCancellation(Ct))
                        {
                        }
                    }
                },
                Ct);
        }

        private static async Task EndedAsync(Task reading)
        {
            try
            {
                await reading.WaitAsync(TimeSpan.FromSeconds(5), Ct);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }
}
