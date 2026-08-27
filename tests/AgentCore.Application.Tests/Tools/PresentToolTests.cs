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
        using var scope = TurnAmbients.Amend(ambients => ambients with { Screen = screen });

        var result = await Invoke(PresentTool.Create("draw"), """
            { "$type": "Card", "children": [{ "$type": "Text", "children": ["hi"] }] }
            """);

        Assert.Equal(PresentTool.RendererName, Assert.Single(screen.Published).Name);
        Assert.False(result.ContainsKey(ToolErrorResult.ErrorProperty));
    }

    [Fact]
    public async Task Present_AnUnknownComponent_ReturnsAnErrorAndDrawsNothing()
    {
        RecordingRenderPort screen = new();
        using var scope = TurnAmbients.Amend(ambients => ambients with { Screen = screen });

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
        using var scope = TurnAmbients.Amend(ambients => ambients with { Screen = screen });

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
    public async Task Present_ANumericType_ReturnsAnActionableErrorAndDrawsNothing()
    {
        RecordingRenderPort screen = new();
        using var scope = TurnAmbients.Amend(ambients => ambients with { Screen = screen });

        var result = await Invoke(PresentTool.Create("draw"), """{ "$type": 123 }""");

        Assert.Empty(screen.Published);
        Assert.True(result[ToolErrorResult.ErrorProperty]!.GetValue<bool>());
        var message = result[ToolErrorResult.MessageProperty]!.GetValue<string>();
        Assert.Contains("$type", message, StringComparison.Ordinal);
        Assert.Contains("not a string", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Present_ANonStringActionType_ReturnsAnActionableErrorAndDrawsNothing()
    {
        RecordingRenderPort screen = new();
        using var scope = TurnAmbients.Amend(ambients => ambients with { Screen = screen });

        var result = await Invoke(
            PresentTool.Create("draw"),
            """{ "$type": "Button", "label": "Yes", "$action": { "type": 42 } }""");

        Assert.Empty(screen.Published);
        Assert.True(result[ToolErrorResult.ErrorProperty]!.GetValue<bool>());
        var message = result[ToolErrorResult.MessageProperty]!.GetValue<string>();
        Assert.Contains("$action", message, StringComparison.Ordinal);
        Assert.Contains("Button", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>$key</c> is reserved, so <see cref="DrawingTree.Validate"/>'s prop loop never looks inside
    /// it, while the receipt walks every key but <c>$action</c> and does. Reading the <c>type</c>
    /// there unguarded threw <see cref="InvalidOperationException"/> from inside the publish block —
    /// after the tree was already on the caller's screen.
    /// </summary>
    [Fact]
    public async Task Present_ANonStringActionTypeUnderAReservedKey_FinishesWithoutThrowing()
    {
        RecordingRenderPort screen = new();
        using var scope = TurnAmbients.Amend(ambients => ambients with { Screen = screen });

        var result = await Invoke(
            PresentTool.Create("draw"), """{ "$type": "Card", "$key": { "$action": { "type": 42 } } }""");

        // The validator accepts this tree, so the tool must finish it: one publish, one receipt, and
        // no button named off a type that is not a string. What must never happen again is the
        // half-done state — a published tree and an error result for the same call.
        Assert.False(result.ContainsKey(ToolErrorResult.ErrorProperty));
        Assert.Single(screen.Published);
        Assert.Contains("buttons: none", result["drew"]!.GetValue<string>(), StringComparison.Ordinal);
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
