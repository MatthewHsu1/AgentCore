using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Runtime;
using AgentCore.Application.Tests.Runtime;
using AgentCore.Application.Tools;
using AgentCore.Application.Tools.Drawing;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Tools;

/// <summary>
/// The inner <c>present</c> tool: it validates the tree itself and returns a section 8.7 error, so
/// the agent loop that calls it can retry on its own.
/// </summary>
public sealed class PresentToolTests
{
    [Fact]
    public async Task Present_AValidTree_PublishesItAndReturnsTheReceipt()
    {
        RecordingRenderPort screen = new();
        using var scope = CallRenderScope.Enter(screen);

        var result = await Invoke(PresentTool.Create("draw"), """
            { "$type": "Card", "children": [{ "$type": "Text", "children": ["hi"] }] }
            """);

        Assert.Equal(PresentTool.RendererName, Assert.Single(screen.Published).Name);
        Assert.DoesNotContain(ToolErrorResult.ErrorProperty, result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Present_AnUnknownComponent_ReturnsAnErrorAndDrawsNothing()
    {
        RecordingRenderPort screen = new();
        using var scope = CallRenderScope.Enter(screen);

        var result = await Invoke(PresentTool.Create("draw"), """{ "$type": "Wombat" }""");

        Assert.Empty(screen.Published);
        Assert.True(result[ToolErrorResult.ErrorProperty]!.GetValue<bool>());
        Assert.Contains("Wombat", result[ToolErrorResult.MessageProperty]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Present_NoScreen_ReturnsAnError()
    {
        var result = await Invoke(PresentTool.Create("draw"), """{ "$type": "Card" }""");

        Assert.True(result[ToolErrorResult.ErrorProperty]!.GetValue<bool>());
    }

    [Fact]
    public async Task Present_ATreeThatIsNotAnObject_ReturnsAnErrorAndDrawsNothing()
    {
        RecordingRenderPort screen = new();
        using var scope = CallRenderScope.Enter(screen);

        var result = await Invoke(PresentTool.Create("draw"), "[1, 2, 3]");

        Assert.Empty(screen.Published);
        Assert.True(result[ToolErrorResult.ErrorProperty]!.GetValue<bool>());
    }

    [Fact]
    public async Task Present_NamesTheDeclaredToolInTheErrorResult()
    {
        var result = await Invoke(PresentTool.Create("draw"), """{ "$type": "Wombat" }""");

        Assert.Equal("draw", result[ToolErrorResult.ToolProperty]!.GetValue<string>());
    }

    [Fact]
    public void TheTool_AdvertisesNoResultSchema()
    {
        var tool = PresentTool.Create("draw");

        Assert.Equal(PresentTool.Name, tool.Name);
        Assert.Null(tool.ReturnJsonSchema);
    }

    /// <summary>Calls the tool with one tree argument and reads the result as a node tree.</summary>
    private static async Task<JsonObject> Invoke(AIFunction tool, string treeJson)
    {
        var arguments = new AIFunctionArguments
        {
            ["tree"] = JsonSerializer.Deserialize<JsonElement>(treeJson),
        };

        var result = await tool.InvokeAsync(arguments, TestContext.Current.CancellationToken);

        return JsonSerializer.SerializeToNode(result)!.AsObject();
    }
}
