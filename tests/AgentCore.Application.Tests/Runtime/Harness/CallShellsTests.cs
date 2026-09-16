using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Runtime;
using AgentCore.Application.Runtime.Harness;
using Microsoft.Agents.AI.Tools.Shell;
using Xunit;

namespace AgentCore.Application.Tests.Runtime.Harness;

/// <summary>
/// <see cref="CallShells"/>: one executor per <see cref="CallShellOptions"/> instance, created on
/// first ask, torn down together.
/// </summary>
public sealed class CallShellsTests : IDisposable
{
    private readonly string _workspace =
        Directory.CreateTempSubdirectory("call-shells-tests-").FullName;

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
    public async Task Get_Local_RunsInTheCallsWorkspace()
    {
        await using CallShells shells = new(_workspace, logger: null);
        CallShellOptions options = new(ShellKind.Local, Policy: null, Timeout: null);

        var result = await shells.Get(options).RunAsync("pwd", TestContext.Current.CancellationToken);

        var expected = new DirectoryInfo(_workspace).FullName.TrimEnd('/');
        Assert.Contains(expected, result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_Local_TimeoutSignalsWithoutThrowing_AndReturnsUnder3Seconds()
    {
        await using CallShells shells = new(_workspace, logger: null);
        CallShellOptions options = new(ShellKind.Local, Policy: null, Timeout: TimeSpan.FromSeconds(1));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var result = await shells.Get(options).RunAsync("sleep 3", TestContext.Current.CancellationToken);
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3),
            $"expected the timed-out run to return in under 3s; took {stopwatch.Elapsed}");
        Assert.True(result is { TimedOut: true, ExitCode: 124 },
            $"expected TimedOut=true and ExitCode=124; got TimedOut={result.TimedOut}, ExitCode={result.ExitCode}");
    }

    [Fact]
    public async Task Get_Local_DenyPolicyRejectsTheCommand_AndTheFileSurvives()
    {
        await using CallShells shells = new(_workspace, logger: null);
        CallShellOptions options = new(
            ShellKind.Local, new ShellPolicy(denyList: ["^rm "]), Timeout: null);

        var file = Path.Combine(_workspace, "keepme.txt");
        await File.WriteAllTextAsync(file, "do not delete", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ShellCommandRejectedException>(
            () => shells.Get(options).RunAsync($"rm -f {file}", TestContext.Current.CancellationToken));

        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task Get_NullAllowListPlusOneDeny_AllowsWhatItDoesNotDeny()
    {
        // This proves ShellPolicy's own semantics directly, at the CallShells layer: a null
        // allowList disables the allow check entirely, so only the deny list refuses a command.
        // ShellPolicy also treats a supplied-but-empty allow list as deny-all (per its own remarks),
        // which is why AgentHarnessProviders.BuildPolicy must translate an agent's empty allow: into
        // null rather than pass it through — that compile-time translation, and the end-to-end
        // consequence of getting it wrong (an undenied command like pwd being refused), is proven in
        // CallSessionShellTests (a shell: policy with deny: and no allow: still lets pwd run).
        await using CallShells shells = new(_workspace, logger: null);
        CallShellOptions options = new(
            ShellKind.Local, new ShellPolicy(denyList: ["^rm "], allowList: null), Timeout: null);

        var ok = await shells.Get(options).RunAsync("echo hi", TestContext.Current.CancellationToken);
        Assert.Contains("hi", ok.Stdout, StringComparison.Ordinal);

        await Assert.ThrowsAsync<ShellCommandRejectedException>(
            () => shells.Get(options).RunAsync("rm -f x", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Get_SameOptionsTwice_ReturnsTheSameExecutorInstance()
    {
        await using CallShells shells = new(_workspace, logger: null);
        CallShellOptions options = new(ShellKind.Local, Policy: null, Timeout: null);

        var first = shells.Get(options);
        var second = shells.Get(options);

        Assert.Same(first, second);
    }

    [Fact]
    public async Task Get_TwoDifferentOptions_ReturnsTwoDifferentExecutors()
    {
        await using CallShells shells = new(_workspace, logger: null);
        CallShellOptions a = new(ShellKind.Local, Policy: null, Timeout: null);
        CallShellOptions b = new(ShellKind.Local, Policy: null, Timeout: null);

        Assert.NotSame(shells.Get(a), shells.Get(b));
    }

    [Fact]
    public async Task DisposeAsync_TwiceIsFine_AndKillsTheLocalBash()
    {
        CallShells shells = new(_workspace, logger: null);
        CallShellOptions options = new(ShellKind.Local, Policy: null, Timeout: null);

        var pidResult = await shells.Get(options).RunAsync("echo $$", TestContext.Current.CancellationToken);
        var pid = int.Parse(pidResult.Stdout.Trim());
        Assert.True(Directory.Exists($"/proc/{pid}"), "the bash should be alive before dispose");

        await shells.DisposeAsync();
        await shells.DisposeAsync();

        await ProcHelpers.AssertGoneAsync(pid, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Get_AfterDispose_ThrowsObjectDisposedException()
    {
        CallShells shells = new(_workspace, logger: null);
        await shells.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(
            () => shells.Get(new CallShellOptions(ShellKind.Local, Policy: null, Timeout: null)));
    }

    [Fact]
    public async Task Get_Docker_ReturnsADockerShellExecutor_WithNoDockerNeeded()
    {
        // DockerShellExecutor construction is lazy: it never talks to docker until RunAsync, so this
        // needs no docker binary on the test host.
        await using CallShells shells = new(_workspace, logger: null);
        CallShellOptions options = new(ShellKind.Docker, Policy: null, Timeout: null);

        var executor = shells.Get(options);

        Assert.IsType<DockerShellExecutor>(executor);
    }

    [Fact]
    public async Task GetEnvironmentAsync_ProbesThroughThisCallsExecutor_AndCachesTheSnapshot()
    {
        await using CallShells shells = new(_workspace, logger: null);
        CallShellOptions options = new(ShellKind.Local, Policy: null, Timeout: null);

        var first = await shells.GetEnvironmentAsync(options, TestContext.Current.CancellationToken);
        var second = await shells.GetEnvironmentAsync(options, TestContext.Current.CancellationToken);

        Assert.Same(first, second);
        Assert.Contains(
            new DirectoryInfo(_workspace).FullName, first.WorkingDirectory, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetEnvironmentAsync_WithADenyAllPolicy_StillReturnsARenderableSnapshot()
    {
        // Every probe a deny-all policy refuses comes back a null field rather than a throw
        // (MAF's own swallowing), so the instructions still render their header.
        await using CallShells shells = new(_workspace, logger: null);
        CallShellOptions options = new(ShellKind.Local, new ShellPolicy(denyList: ["^."]), Timeout: null);

        var snapshot = await shells.GetEnvironmentAsync(options, TestContext.Current.CancellationToken);
        var text = ShellEnvironmentProvider.DefaultInstructionsFormatter(snapshot);

        Assert.StartsWith("## Shell environment", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetEnvironmentAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        CallShells shells = new(_workspace, logger: null);
        await shells.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => shells.GetEnvironmentAsync(
            new CallShellOptions(ShellKind.Local, Policy: null, Timeout: null),
            TestContext.Current.CancellationToken));
    }
}
