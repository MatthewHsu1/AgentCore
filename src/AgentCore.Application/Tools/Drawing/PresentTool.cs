using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Runtime;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tools.Drawing;

/// <summary>
/// The inner tool the drawing agent calls. It is never declared in a document.
/// </summary>
internal static class PresentTool
{
    /// <summary>The name the drawing agent calls.</summary>
    internal const string Name = "present";

    /// <summary>The renderer name the browser looks up.</summary>
    internal const string RendererName = "generative-ui";

    /// <summary>Builds the inner tool for one declared drawing tool.</summary>
    internal static AIFunction Create(string toolId)
        => AIFunctionFactory.Create(
            ([Description("The tree to draw. One object with $type and its props, nested with children.")]
             JsonElement tree) => Publish(toolId, tree),
            new AIFunctionFactoryOptions
            {
                Name = Name,
                Description = "Draw one tree for the caller. Call this once.",
                ExcludeResultSchema = true,
            });

    private static JsonObject Publish(string toolId, JsonElement tree)
    {
        if (CallRenderScope.Current is not { } screen)
        {
            return ToolErrorResult.Create(
                toolId, "this call has no screen, so nothing can be drawn on it. Say it in words instead.");
        }

        try
        {
            if (JsonSerializer.SerializeToNode(tree) is not JsonObject node)
            {
                return ToolErrorResult.Create(toolId, "the tree was not a JSON object.");
            }

            if (DrawingTree.Validate(node) is { } fault)
            {
                return ToolErrorResult.Create(toolId, $"that tree is not valid: {fault} Fix it and call {Name} again.");
            }

            // The receipt is read off the tree first so that Publish is the last statement that can
            // run: anything that fails after it would leave the drawing on the caller's screen and
            // still answer the model an error.
            var receipt = DrawingReceipt.Describe(node);

            // The outer tool call is stable across every retry the drawing agent's own tool loop
            // makes for this one call, so a rejected tree followed by an accepted one replaces the
            // drawing rather than leaving both behind. The ?? toolId fallback only matters to a port
            // with no rule against an absent outer call: the shipped TurnRenders discards a publish
            // with none open regardless of the id, so in production this key is never read back.
            screen.Publish(RendererName, OuterToolCall.Current ?? toolId, node);

            return new JsonObject { ["drew"] = receipt };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return ToolErrorResult.Create(toolId, $"the tree could not be drawn: {exception.Message}");
        }
    }
}
