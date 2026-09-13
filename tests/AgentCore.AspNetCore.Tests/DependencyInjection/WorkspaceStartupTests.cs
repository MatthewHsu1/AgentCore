using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.AspNetCore.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static AgentCore.AspNetCore.Tests.DependencyInjection.StartedHostFixture;

namespace AgentCore.AspNetCore.Tests.DependencyInjection;

/// <summary>
/// <see cref="AgentCoreOptions.UseWorkspace(string)"/> and the folder it binds a call's
/// <see cref="CallSession.Workspace"/> to.
/// </summary>
public sealed class WorkspaceStartupTests : IDisposable
{
    private readonly string _tempRoot =
        Path.Combine(Path.GetTempPath(), "agentcore-ws-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void UseWorkspace_AnEmptyPath_ThrowsArgumentException()
    {
        AgentCoreOptions options = new();

        Assert.Throws<ArgumentException>(() => options.UseWorkspace(""));
    }

    [Fact]
    public async Task AHostBoundToAWorkspaceRoot_CreatesTheCallsFolderWhenACallOpens()
    {
        using var provider = await BuildAsync(OneAgentYaml, options => options.UseWorkspace(_tempRoot));

        var sessions = provider.GetRequiredService<ICallSessions>();
        var session = await sessions.OpenAsync("call-1", TestContext.Current.CancellationToken);

        Assert.Equal(Path.Combine(_tempRoot, "call-1"), session.Workspace);
        Assert.True(Directory.Exists(Path.Combine(_tempRoot, "call-1")));
    }

    [Fact]
    public async Task AHostWithNoWorkspaceBound_CreatesNothingAndLeavesWorkspaceNull()
    {
        using var provider = await BuildAsync(OneAgentYaml);

        var sessions = provider.GetRequiredService<ICallSessions>();
        var session = await sessions.OpenAsync("call-1", TestContext.Current.CancellationToken);

        Assert.Null(session.Workspace);
    }

    [Fact]
    public async Task ARootThatCannotBeCreated_FailsStartupWithThePathAndTheOption()
    {
        var blockingFile = Path.Combine(_tempRoot, "blocks-the-root");
        Directory.CreateDirectory(_tempRoot);
        await File.WriteAllTextAsync(blockingFile, "not a directory", TestContext.Current.CancellationToken);
        var unusableRoot = Path.Combine(blockingFile, "x");

        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
            () => BuildAsync(OneAgentYaml, options => options.UseWorkspace(unusableRoot)));

        Assert.Contains("options.UseWorkspace(", failure.Message, StringComparison.Ordinal);
        Assert.Contains(unusableRoot, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMemoryAgent_WithAWorkspaceRootBound_Starts()
    {
        using var provider = await BuildAsync(MemoryAgentYaml, options => options.UseWorkspace(_tempRoot));

        var sessions = provider.GetRequiredService<ICallSessions>();
        var session = await sessions.OpenAsync("call-1", TestContext.Current.CancellationToken);

        Assert.StartsWith(_tempRoot, session.Workspace, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMemoryAgent_WithNoWorkspaceRootBound_FailsStartupNamingUseWorkspace()
    {
        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
            () => BuildAsync(MemoryAgentYaml));

        Assert.Contains("options.UseWorkspace(", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMemoryAgent_WithARootThatCannotBeCreated_FailsStartupWithThePathAndTheOption()
    {
        var blockingFile = Path.Combine(_tempRoot, "blocks-the-root");
        Directory.CreateDirectory(_tempRoot);
        await File.WriteAllTextAsync(blockingFile, "not a directory", TestContext.Current.CancellationToken);
        var unusableRoot = Path.Combine(blockingFile, "x");

        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
            () => BuildAsync(MemoryAgentYaml, options => options.UseWorkspace(unusableRoot)));

        Assert.Contains("options.UseWorkspace(", failure.Message, StringComparison.Ordinal);
        Assert.Contains(unusableRoot, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFilesAgent_WithAWorkspaceRootBound_Starts()
    {
        using var provider = await BuildAsync(FilesAgentYaml, options => options.UseWorkspace(_tempRoot));

        var sessions = provider.GetRequiredService<ICallSessions>();
        var session = await sessions.OpenAsync("call-1", TestContext.Current.CancellationToken);

        Assert.StartsWith(_tempRoot, session.Workspace, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFilesAgent_WithNoWorkspaceRootBound_FailsStartupNamingUseWorkspace()
    {
        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
            () => BuildAsync(FilesAgentYaml));

        Assert.Contains("options.UseWorkspace(", failure.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private const string MemoryAgentYaml =
        """
        apiVersion: agentcore/v1
        name: composed
        agents:
          items:
            - { id: only, instructions: "I answer everything", memory: { store: workspace } }
        providers:
          call:   { kind: telnyx-relay }
          speech:
            stt: { kind: telnyx-relay }
            tts: { kind: telnyx-relay }
          llm:
            - { kind: openai, model: gpt-4.1-mini, as: reply }
        """;

    private const string FilesAgentYaml =
        """
        apiVersion: agentcore/v1
        name: composed
        agents:
          items:
            - { id: only, instructions: "I answer everything", files: { store: workspace } }
        providers:
          call:   { kind: telnyx-relay }
          speech:
            stt: { kind: telnyx-relay }
            tts: { kind: telnyx-relay }
          llm:
            - { kind: openai, model: gpt-4.1-mini, as: reply }
        """;
}
