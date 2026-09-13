using AgentCore.Application.Runtime;
using AgentCore.Application.Runtime.Harness;
using AgentCore.Application.Runtime.Turn;
using AgentCore.Application.Tests.Runtime.Turn;
using Xunit;

namespace AgentCore.Application.Tests.Runtime.Harness;

/// <summary>
/// <see cref="CallScopedAgentFileStore"/> as a unit: it resolves the running call's workspace off
/// <see cref="TurnAmbients"/> at each operation, with no root and no cache of its own.
/// </summary>
public sealed class CallScopedAgentFileStoreTests
{
    [Fact]
    public async Task NoAmbientWorkspace_ReadAsyncThrowsInvalidOperationException()
    {
        var store = new CallScopedAgentFileStore();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.ReadAsync("out.txt", TestContext.Current.CancellationToken));

        Assert.Contains("files: tool", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AmbientWorkspaceOpen_WriteThenReadRoundTrips_UnderThatWorkspace()
    {
        var workspace = Directory.CreateTempSubdirectory("call-scoped-file-store-").FullName;
        try
        {
            var store = new CallScopedAgentFileStore();

            using (TurnAmbientsTestScope.WithWorkspace(workspace))
            {
                await store.WriteAsync("out.txt", "hello", TestContext.Current.CancellationToken);
                var read = await store.ReadAsync("out.txt", TestContext.Current.CancellationToken);

                Assert.Equal("hello", read);
            }

            Assert.Equal("hello", await File.ReadAllTextAsync(
                Path.Combine(workspace, "out.txt"), TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }
}
