using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Runtime;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tools.Drawing;

/// <summary>
/// The inner tool the drawing agent calls. It is never declared in a document.
/// </summary>
/// <remarks>
/// <para>
/// The argument is one free-form object. The shape it must hold is in the agent's instructions, not
/// here: a declared shape would cost the 19 KB the prose vocabulary avoids. Nothing constrains the
/// model's output as a result, so <see cref="DrawingTree.Validate"/> is the only thing standing
/// between the model and the renderer.
/// </para>
/// <para>
/// A bad tree comes back as a section 8.7 error rather than as a thrown exception, which is what
/// lets the agent's own tool loop retry it.
/// </para>
/// </remarks>
internal static class PresentTool
{
    /// <summary>The name the drawing agent calls.</summary>
    internal const string Name = "present";

    /// <summary>The renderer name the browser looks up.</summary>
    internal const string RendererName = "generative-ui";

    /// <summary>Builds the inner tool for one declared drawing tool.</summary>
    /// <param name="toolId">The declared tool id, so an error result names what the caller declared.</param>
    /// <returns>The tool.</returns>
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

            screen.Publish(RendererName, node);

            return new JsonObject { ["drew"] = receipt };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Section 8.7: nothing here may end the turn, including a fault DrawingTree.Validate
            // itself did not anticipate.
            return ToolErrorResult.Create(toolId, $"the tree could not be drawn: {exception.Message}");
        }
    }
}
