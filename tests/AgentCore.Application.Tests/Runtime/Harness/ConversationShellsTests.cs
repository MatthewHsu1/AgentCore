using System.Diagnostics;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Runtime.Harness;
using Microsoft.Agents.AI.Tools.Shell;
using Xunit;

namespace AgentCore.Application.Tests.Runtime.Harness
{
    /// <summary>
    /// <see cref="ConversationShells"/>: one executor per <see cref="ConversationShellOptions"/> instance, created on
    /// first ask, torn down together.
    /// </summary>
    public sealed class ConversationShellsTests : IDisposable
    {
        private readonly string _workspace =
            Directory.CreateTempSubdirectory("conversation-shells-tests-").FullName;

        public void Dispose()
        {
            try
            {
                Directory.Delete(_workspace, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        [Fact]
        public async Task Get_Local_RunsInTheConversationsWorkspace()
        {
            await using ConversationShells shells = new(_workspace, logger: null);
            ConversationShellOptions options = new(ShellKind.Local, Policy: null, Timeout: null);

            ShellResult result = await shells.Get(options).RunAsync("pwd", TestContext.Current.CancellationToken);

            string expected = new DirectoryInfo(_workspace).FullName.TrimEnd('/');
            Assert.Contains(expected, result.Stdout, StringComparison.Ordinal);
        }

        // The command runs 30 times the timeout, so a run that waited for it cannot pass even on a loaded machine.
        [Fact]
        public async Task Get_Local_TimeoutSignalsWithoutThrowing_AndReturnsBeforeTheCommandEnds()
        {
            await using ConversationShells shells = new(_workspace, logger: null);
            ConversationShellOptions options = new(ShellKind.Local, Policy: null, Timeout: TimeSpan.FromSeconds(1));

            Stopwatch stopwatch = Stopwatch.StartNew();
            ShellResult result = await shells.Get(options).RunAsync("sleep 30", TestContext.Current.CancellationToken);
            stopwatch.Stop();

            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(20),
                $"expected the timed-out run to return well before the 30 s command ended; took {stopwatch.Elapsed}");
            Assert.True(result is { TimedOut: true, ExitCode: 124 },
                $"expected TimedOut=true and ExitCode=124; got TimedOut={result.TimedOut}, ExitCode={result.ExitCode}");
        }

        [Fact]
        public async Task Get_Local_DenyPolicyRejectsTheCommand_AndTheFileSurvives()
        {
            await using ConversationShells shells = new(_workspace, logger: null);
            ConversationShellOptions options = new(
                ShellKind.Local, new ShellPolicy(denyList: ["^rm "]), Timeout: null);

            string file = Path.Combine(_workspace, "keepme.txt");
            await File.WriteAllTextAsync(file, "do not delete", TestContext.Current.CancellationToken);

            _ = await Assert.ThrowsAsync<ShellCommandRejectedException>(
                () => shells.Get(options).RunAsync($"rm -f {file}", TestContext.Current.CancellationToken));

            Assert.True(File.Exists(file));
        }

        [Fact]
        public async Task Get_NullAllowListPlusOneDeny_AllowsWhatItDoesNotDeny()
        {
            // This proves ShellPolicy's own semantics directly, at the ConversationShells layer: a null
            // allowList disables the allow check entirely, so only the deny list refuses a command.
            // ShellPolicy also treats a supplied-but-empty allow list as deny-all,
            // which is why AgentHarnessProviders.BuildPolicy must translate an agent's empty allow: into
            // null rather than pass it through — that compile-time translation, and the end-to-end
            // consequence of getting it wrong (an undenied command like pwd being refused), is proven in
            // ConversationSessionShellTests (a shell: policy with deny: and no allow: still lets pwd run).
            await using ConversationShells shells = new(_workspace, logger: null);
            ConversationShellOptions options = new(
                ShellKind.Local, new ShellPolicy(denyList: ["^rm "], allowList: null), Timeout: null);

            ShellResult ok = await shells.Get(options).RunAsync("echo hi", TestContext.Current.CancellationToken);
            Assert.Contains("hi", ok.Stdout, StringComparison.Ordinal);

            _ = await Assert.ThrowsAsync<ShellCommandRejectedException>(
                () => shells.Get(options).RunAsync("rm -f x", TestContext.Current.CancellationToken));
        }

        [Fact]
        public async Task Get_SameOptionsTwice_ReturnsTheSameExecutorInstance()
        {
            await using ConversationShells shells = new(_workspace, logger: null);
            ConversationShellOptions options = new(ShellKind.Local, Policy: null, Timeout: null);

            ShellExecutor first = shells.Get(options);
            ShellExecutor second = shells.Get(options);

            Assert.Same(first, second);
        }

        [Fact]
        public async Task Get_TwoDifferentOptions_ReturnsTwoDifferentExecutors()
        {
            await using ConversationShells shells = new(_workspace, logger: null);
            ConversationShellOptions a = new(ShellKind.Local, Policy: null, Timeout: null);
            ConversationShellOptions b = new(ShellKind.Local, Policy: null, Timeout: null);

            Assert.NotSame(shells.Get(a), shells.Get(b));
        }

        [Fact]
        public async Task DisposeAsync_TwiceIsFine_AndKillsTheLocalBash()
        {
            ConversationShells shells = new(_workspace, logger: null);
            ConversationShellOptions options = new(ShellKind.Local, Policy: null, Timeout: null);

            ShellResult pidResult = await shells.Get(options).RunAsync("echo $$", TestContext.Current.CancellationToken);
            int pid = int.Parse(pidResult.Stdout.Trim());
            Assert.True(Directory.Exists($"/proc/{pid}"), "the bash should be alive before dispose");

            await shells.DisposeAsync();
            await shells.DisposeAsync();

            await ProcHelpers.AssertGoneAsync(pid, TestContext.Current.CancellationToken);
        }

        [Fact]
        public async Task Get_AfterDispose_ThrowsObjectDisposedException()
        {
            ConversationShells shells = new(_workspace, logger: null);
            await shells.DisposeAsync();

            _ = Assert.Throws<ObjectDisposedException>(
                () => shells.Get(new ConversationShellOptions(ShellKind.Local, Policy: null, Timeout: null)));
        }

        [Fact]
        public async Task Get_Local_CleanEnvironment_DropsTheHostsCanaryButKeepsPathAndADeclaredEnvVar()
        {
            Environment.SetEnvironmentVariable("AGENTCORE_TEST_CANARY", "leaked");
            try
            {
                await using ConversationShells shells = new(_workspace, logger: null);
                ConversationShellOptions options = new(
                    ShellKind.Local,
                    Policy: null,
                    Timeout: null,
                    Env: new Dictionary<string, string> { ["PROBE_OK"] = "yes" });

                ShellResult canary = await shells.Get(options).RunAsync(
                    "echo \"c=$AGENTCORE_TEST_CANARY\"", TestContext.Current.CancellationToken);
                Assert.Equal("c=", canary.Stdout.Trim());

                ShellResult declared = await shells.Get(options).RunAsync(
                    "echo $PROBE_OK", TestContext.Current.CancellationToken);
                Assert.Equal("yes", declared.Stdout.Trim());

                ShellResult path = await shells.Get(options).RunAsync("ls / >/dev/null && echo ok", TestContext.Current.CancellationToken);
                Assert.Equal("ok", path.Stdout.Trim());
            }
            finally
            {
                Environment.SetEnvironmentVariable("AGENTCORE_TEST_CANARY", null);
            }
        }

        [Fact]
        public async Task Get_Docker_ReturnsADockerShellExecutor_WithNoDockerNeeded()
        {
            // DockerShellExecutor construction is lazy: it never talks to docker until RunAsync, so this
            // needs no docker binary on the test host.
            await using ConversationShells shells = new(_workspace, logger: null);
            ConversationShellOptions options = new(ShellKind.Docker, Policy: null, Timeout: null);

            ShellExecutor executor = shells.Get(options);

            _ = Assert.IsType<DockerShellExecutor>(executor);
        }

        [Fact]
        public async Task GetEnvironmentAsync_ProbesThroughThisConversationsExecutor_AndCachesTheSnapshot()
        {
            await using ConversationShells shells = new(_workspace, logger: null);
            ConversationShellOptions options = new(ShellKind.Local, Policy: null, Timeout: null);

            ShellEnvironmentSnapshot first = await shells.GetEnvironmentAsync(options, TestContext.Current.CancellationToken);
            ShellEnvironmentSnapshot second = await shells.GetEnvironmentAsync(options, TestContext.Current.CancellationToken);

            Assert.Same(first, second);
            Assert.Contains(
                new DirectoryInfo(_workspace).FullName, first.WorkingDirectory, StringComparison.Ordinal);
        }

        [Fact]
        public async Task GetEnvironmentAsync_WithADenyAllPolicy_StillReturnsARenderableSnapshot()
        {
            // Every probe a deny-all policy refuses comes back a null field rather than a throw
            // (MAF's own swallowing), so the instructions still render their header.
            await using ConversationShells shells = new(_workspace, logger: null);
            ConversationShellOptions options = new(ShellKind.Local, new ShellPolicy(denyList: ["^."]), Timeout: null);

            ShellEnvironmentSnapshot snapshot = await shells.GetEnvironmentAsync(options, TestContext.Current.CancellationToken);
            string text = ShellEnvironmentProvider.DefaultInstructionsFormatter(snapshot);

            Assert.StartsWith("## Shell environment", text, StringComparison.Ordinal);
        }

        [Fact]
        public async Task GetEnvironmentAsync_AfterDispose_ThrowsObjectDisposedException()
        {
            ConversationShells shells = new(_workspace, logger: null);
            await shells.DisposeAsync();

            _ = await Assert.ThrowsAsync<ObjectDisposedException>(() => shells.GetEnvironmentAsync(
                new ConversationShellOptions(ShellKind.Local, Policy: null, Timeout: null),
                TestContext.Current.CancellationToken));
        }
    }
}
