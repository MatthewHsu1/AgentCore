#pragma warning disable MAAI001 // The context constructors are evaluation-only in Microsoft.Agents.AI 1.21.0.

using AgentCore.Application.Transcript;
using AgentCore.Application.Blobs;
using AgentCore.Application.Runtime;
using AgentCore.Application.Runtime.Harness;
using AgentCore.Application.Runtime.Turn;
using AgentCore.Application.Tests.Fakes;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentCore.Application.Tests.Runtime.Harness;

/// <summary>
/// After a run: every sandbox file the vendor surfaced is downloaded and stored under the call, and
/// the content it rode in on says what was kept.
/// </summary>
public sealed class HostedFileCaptureProviderTests
{
    private static readonly byte[] Png = [0x89, (byte)'P', (byte)'N', (byte)'G', 1, 2, 3];

    [Fact]
    public async Task InvokedAsync_FileOnTheMessage_StoresItUnderTheCallAndTheName()
    {
        // Arrange
        var files = new ScriptedHostedFileClient().Serve("cfile_1", Png, "image/png");
        RecordingBlobStore blobs = new();
        var (agent, session) = await AgentWithFiledTurn("call-7");
        var response = Reply(File("cfile_1", "chart.png", "cntr_1"));

        // Act
        await Provider(files, blobs).InvokedAsync(new AIContextProvider.InvokedContext(agent, session, [], response), TestContext.Current.CancellationToken);

        // Assert
        var stored = Assert.Single(blobs.Blobs);
        Assert.Equal(("call-7", "chart.png"), stored.Key);
        Assert.Equal("image/png", stored.Value.MediaType);
        Assert.Equal(Png, stored.Value.Bytes);
        Assert.Equal("cntr_1", files.ScopesAsked["cfile_1"]);
    }

    [Fact]
    public async Task InvokedAsync_FileKept_WritesWhatWasStoredOntoTheContent()
    {
        // Arrange: the vendor names no media type; the download does.
        var files = new ScriptedHostedFileClient().Serve("cfile_1", Png, "image/png");
        RecordingBlobStore blobs = new();
        var (agent, session) = await AgentWithFiledTurn("call-7");
        var file = File("cfile_1", "chart.png");

        // Act
        await Provider(files, blobs).InvokedAsync(new AIContextProvider.InvokedContext(agent, session, [], Reply(file)), TestContext.Current.CancellationToken);

        // Assert: a later read links from the content alone.
        Assert.True(file.Kept);
        Assert.Equal("image/png", file.MediaType);
        Assert.Equal(Png.Length, file.Length);
    }

    [Fact]
    public async Task InvokedAsync_FileRefused_LeavesTheContentNotKept()
    {
        // Arrange: a 100-byte cap, a 300-byte file.
        var files = new ScriptedHostedFileClient().Serve("cfile_6", new byte[300], "image/png");
        var (agent, session) = await AgentWithFiledTurn("call-7");
        var file = File("cfile_6", "big.png");
        var provider = new HostedFileCaptureProvider(files, new RecordingBlobStore(), new BlobPolicy(100, ["png"]), NullLogger.Instance);

        // Act
        await provider.InvokedAsync(new AIContextProvider.InvokedContext(agent, session, [], Reply(file)), TestContext.Current.CancellationToken);

        // Assert
        Assert.False(file.Kept);
    }

    [Fact]
    public async Task InvokedAsync_ChildSessionWithTheOwnerStamp_StoresUnderThatCall()
    {
        // Arrange: no turn filed, only the stamp BackgroundChildAgent leaves.
        var files = new ScriptedHostedFileClient().Serve("cfile_3", Png, "image/png");
        RecordingBlobStore blobs = new();
        var agent = Agent();
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        session.StateBag.SetValue(BlobOwnerKey.Value, "call-child");
        var response = Reply(File("cfile_3", "rows.csv"));

        // Act
        await Provider(files, blobs).InvokedAsync(new AIContextProvider.InvokedContext(agent, session, [], response), TestContext.Current.CancellationToken);

        // Assert
        Assert.True(blobs.Blobs.ContainsKey(("call-child", "rows.csv")));
    }

    [Fact]
    public async Task InvokedAsync_NoOwnerAtAll_StoresNothingAndDoesNotThrow()
    {
        // Arrange
        var files = new ScriptedHostedFileClient().Serve("cfile_4", Png, "image/png");
        RecordingBlobStore blobs = new();
        var agent = Agent();
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        var response = Reply(File("cfile_4", "chart.png"));

        // Act
        await Provider(files, blobs).InvokedAsync(new AIContextProvider.InvokedContext(agent, session, [], response), TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(blobs.Blobs);
    }

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("page.html")]
    public async Task InvokedAsync_UnsafeNameOrDisallowedExtension_IsRefusedNotStored(string name)
    {
        // Arrange
        var files = new ScriptedHostedFileClient().Serve("cfile_5", Png, "image/png");
        RecordingBlobStore blobs = new();
        var (agent, session) = await AgentWithFiledTurn("call-7");
        var response = Reply(File("cfile_5", name));

        // Act
        await Provider(files, blobs).InvokedAsync(new AIContextProvider.InvokedContext(agent, session, [], response), TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(blobs.Blobs);
    }

    [Fact]
    public async Task InvokedAsync_FileOverTheCap_IsRefusedWithoutBufferingAllOfIt()
    {
        // Arrange: a 100-byte cap, a 300-byte file.
        var files = new ScriptedHostedFileClient().Serve("cfile_6", new byte[300], "image/png");
        RecordingBlobStore blobs = new();
        var (agent, session) = await AgentWithFiledTurn("call-7");
        var response = Reply(File("cfile_6", "big.png"));
        var provider = new HostedFileCaptureProvider(files, blobs, new BlobPolicy(100, ["png"]), NullLogger.Instance);

        // Act
        await provider.InvokedAsync(new AIContextProvider.InvokedContext(agent, session, [], response), TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(blobs.Blobs);
    }

    [Fact]
    public async Task InvokedAsync_DownloadThrows_StoresNothingAndDoesNotThrow()
    {
        // Arrange: the client knows no such file.
        ScriptedHostedFileClient files = new();
        RecordingBlobStore blobs = new();
        var (agent, session) = await AgentWithFiledTurn("call-7");
        var response = Reply(File("cfile_missing", "chart.png"));

        // Act
        await Provider(files, blobs).InvokedAsync(new AIContextProvider.InvokedContext(agent, session, [], response), TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(blobs.Blobs);
    }

    [Fact]
    public async Task InvokingAsync_TellsTheModelWhereToSaveAndHowToLink()
    {
        // Arrange
        var agent = Agent();
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);

        // Act
        var context = await Provider(new ScriptedHostedFileClient(), new RecordingBlobStore())
            .InvokingAsync(new AIContextProvider.InvokingContext(agent, session, new AIContext()), TestContext.Current.CancellationToken);

        // Assert
        Assert.Contains("/mnt/data", context.Instructions, StringComparison.Ordinal);
        Assert.Contains("sandbox:/mnt/data/<name>", context.Instructions, StringComparison.Ordinal);
    }

    private static HostedFileCaptureProvider Provider(ScriptedHostedFileClient files, RecordingBlobStore blobs)
        => new(files, blobs, BlobPolicy.Default, NullLogger.Instance);

    private static ChatClientAgent Agent() => new ChatClientAgent(new ScriptedChatClient("ok"));

    private static async Task<(AIAgent Agent, AgentSession Session)> AgentWithFiledTurn(string callId)
    {
        var agent = Agent();
        var session = await agent.CreateSessionAsync(TestContext.Current.CancellationToken);
        TurnRegistry.Set(session, new TurnInvocation { CallId = callId, TurnIndex = 0, Stage = "s" });
        return (agent, session);
    }

    private static FileContent File(string fileId, string name, string? scope = null)
        => new() { Name = name, FileId = fileId, Scope = scope };

    private static List<ChatMessage> Reply(params AIContent[] contents)
        => [new ChatMessage(ChatRole.Assistant, [new TextContent("see sandbox:/mnt/data/x"), .. contents])];
}
