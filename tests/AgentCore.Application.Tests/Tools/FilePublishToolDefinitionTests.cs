using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Blobs;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Runtime;
using AgentCore.Application.Runtime.Turn;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tools;
using AgentCore.Application.Tools.Builtin;
using AgentCore.Application.Transcript;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Tools;

/// <summary>
/// The <c>uses: file.publish</c> builtin: what it refuses, and what it stores, links, and files on
/// the turn when it accepts.
/// </summary>
public sealed class FilePublishToolDefinitionTests : IDisposable
{
    private static readonly ToolConfiguration Declared =
        new() { Id = "publish", Kind = ToolKind.Builtin, Uses = BuiltinToolNames.FilePublish, Description = "Publish a file." };

    private readonly string _root = Path.Combine(Path.GetTempPath(), "agentcore-publish-" + Guid.NewGuid().ToString("N"));

    private readonly RecordingBlobStore _blobs = new();

    public FilePublishToolDefinitionTests() => Directory.CreateDirectory(Path.Combine(_root, "call-1"));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Name_IsTheDocumentedUsesValue()
    {
        Assert.Equal("file.publish", BuiltinToolNames.FilePublish);
    }

    [Fact]
    public void Build_NoBlobStore_FailsTheLoadNamingTheToolAndThePort()
    {
        var failure = Assert.Throws<ConfigurationLoadException>(() => new FilePublishToolDefinition().Build(
            Declared, new BuiltinToolPorts(ChatClients: null, Blobs: null, WorkspaceRoot: _root)));

        Assert.Contains("'publish'", failure.Message, StringComparison.Ordinal);
        Assert.Contains("providers.blobs", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_NoWorkspaceRoot_FailsTheLoadNamingUseWorkspace()
    {
        var failure = Assert.Throws<ConfigurationLoadException>(() => new FilePublishToolDefinition().Build(
            Declared, new BuiltinToolPorts(ChatClients: null, Blobs: _blobs, WorkspaceRoot: null)));

        Assert.Contains("'publish'", failure.Message, StringComparison.Ordinal);
        Assert.Contains("options.UseWorkspace(", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_NamesTheFunctionAfterTheDeclaredIdAndHidesTheTurnFromTheModel()
    {
        var function = Assert.IsType<AIFunction>(Build(), exactMatch: false);

        Assert.Equal("publish", function.Name);
        Assert.Equal("Publish a file.", function.Description);

        var properties = function.JsonSchema.GetProperty("properties");
        Assert.True(properties.TryGetProperty("path", out _));
        Assert.True(properties.TryGetProperty("title", out _));
        Assert.False(properties.TryGetProperty("turn", out _));
        Assert.Contains("path", function.JsonSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
    }

    [Theory]
    [InlineData("../secret.csv")]
    [InlineData("out/../../secret.csv")]
    [InlineData("/etc/passwd")]
    public async Task Publish_APathOutsideTheWorkspace_IsRefusedWithAToolError(string path)
    {
        File.WriteAllText(Path.Combine(_root, "secret.csv"), "a,b");

        var result = await PublishAsync(path);

        Assert.True(ToolErrorResult.IsError(result));
        Assert.Equal("publish", result["tool"]!.GetValue<string>());
        Assert.Contains("inside the workspace", result["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Empty(_blobs.Blobs);
    }

    [Fact]
    public async Task Publish_AFileThatIsNotThere_IsRefusedWithAToolError()
    {
        var result = await PublishAsync("missing.csv");

        Assert.True(ToolErrorResult.IsError(result));
        Assert.Contains("missing.csv", result["message"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Publish_AnExtensionThePolicyRefuses_IsRefusedWithTheReason()
    {
        File.WriteAllText(Path.Combine(_root, "call-1", "notes.exe"), "x");

        var result = await PublishAsync("notes.exe");

        Assert.True(ToolErrorResult.IsError(result));
        Assert.Contains("extension 'exe' is not allowed", result["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Empty(_blobs.Blobs);
    }

    [Fact]
    public async Task Publish_AFileOverTheCap_IsRefusedWithTheReason()
    {
        File.WriteAllBytes(Path.Combine(_root, "call-1", "big.csv"), new byte[64]);

        var result = await PublishAsync("big.csv", policy: new BlobPolicy(32, ["csv"]));

        Assert.True(ToolErrorResult.IsError(result));
        Assert.Contains("size 64 is outside 0..32 bytes", result["message"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Publish_AGoodFile_StoresItUnderTheCallLinksItAndFilesTheCardOnTheTurn()
    {
        File.WriteAllText(Path.Combine(_root, "call-1", "rows.csv"), "a,b\n1,2\n");
        TurnFiles files = new();
        using var outer = files.BeginOuterCall("tc_1");

        var result = await PublishAsync("rows.csv", title: "Sales by month", files: files);

        Assert.False(ToolErrorResult.IsError(result));
        Assert.Equal("rows.csv", result["name"]!.GetValue<string>());
        Assert.Equal("text/csv", result["mediaType"]!.GetValue<string>());
        Assert.Equal(8, result["length"]!.GetValue<long>());
        Assert.Equal("https://blobs.test/call-1/rows.csv?ttl=900", result["url"]!.GetValue<string>());

        var (mediaType, bytes) = _blobs.Blobs[("call-1", "rows.csv")];
        Assert.Equal("text/csv", mediaType);
        Assert.Equal("a,b\n1,2\n", System.Text.Encoding.UTF8.GetString(bytes));

        var card = Assert.Single(files.TakeFor("tc_1"));
        Assert.Equal("rows.csv", card.Name);
        Assert.Equal("rows.csv", card.FileId);
        Assert.Equal("Sales by month", card.Title);
        Assert.Equal("text/csv", card.MediaType);
        Assert.Equal(8, card.Length);
        Assert.True(card.Kept);
    }

    [Fact]
    public async Task Publish_AFileInASubfolder_IsStoredUnderItsOwnName()
    {
        Directory.CreateDirectory(Path.Combine(_root, "call-1", "out"));
        File.WriteAllBytes(Path.Combine(_root, "call-1", "out", "chart.png"), [1, 2, 3]);

        var result = await PublishAsync("out/chart.png");

        Assert.Equal("chart.png", result["name"]!.GetValue<string>());
        Assert.Equal("image/png", result["mediaType"]!.GetValue<string>());
        Assert.True(_blobs.Blobs.ContainsKey(("call-1", "chart.png")));
    }

    [Fact]
    public async Task Publish_OutsideAnyTurn_IsRefusedWithAToolError()
    {
        File.WriteAllText(Path.Combine(_root, "call-1", "rows.csv"), "a");

        var result = await PublishAsync("rows.csv", turn: false);

        Assert.True(ToolErrorResult.IsError(result));
        Assert.Contains("no call is running", result["message"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    private AITool Build(BlobPolicy? policy = null)
        => new FilePublishToolDefinition().Build(
            Declared, new BuiltinToolPorts(ChatClients: null, Blobs: _blobs, BlobPolicy: policy, WorkspaceRoot: _root));

    private async Task<JsonObject> PublishAsync(
        string path, string? title = null, BlobPolicy? policy = null, TurnFiles? files = null, bool turn = true)
    {
        var function = (AIFunction)Build(policy);

        Dictionary<string, object?> arguments = new(StringComparer.Ordinal) { ["path"] = path, ["title"] = title };

        if (turn)
        {
            arguments[TurnInvocation.ArgumentsKey] = new TurnInvocation
            {
                CallId = "call-1",
                TurnIndex = 0,
                Stage = string.Empty,
                Workspace = Path.Combine(_root, "call-1"),
                Files = files,
            };
        }

        var result = await function.InvokeAsync(new AIFunctionArguments(arguments), TestContext.Current.CancellationToken);

        return Assert.IsType<JsonObject>(ToolResultJson.ToNode(result));
    }
}
