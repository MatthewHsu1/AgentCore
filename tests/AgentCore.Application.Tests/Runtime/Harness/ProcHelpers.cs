using Xunit;

namespace AgentCore.Application.Tests.Runtime.Harness;

/// <summary>
/// Waits for a Linux process to actually be gone, instead of guessing a fixed delay: dispose is
/// async but the OS reaping a killed process is not synchronous with it.
/// </summary>
internal static class ProcHelpers
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// Polls <c>/proc/&lt;pid&gt;</c> until the process is gone or a zombie, up to <see cref="Timeout"/>,
    /// then asserts it.
    /// </summary>
    public static async Task AssertGoneAsync(int pid, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + Timeout;

        while (!IsGone(pid) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(PollInterval, cancellationToken);
        }

        Assert.True(IsGone(pid), $"the bash pid {pid} should be gone (or a zombie) within {Timeout}");
    }

    private static bool IsGone(int pid) =>
        !Directory.Exists($"/proc/{pid}") || File.ReadAllText($"/proc/{pid}/stat").Contains(" Z ");
}
