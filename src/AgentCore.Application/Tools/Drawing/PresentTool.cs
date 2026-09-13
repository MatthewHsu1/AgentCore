using System.ComponentModel;
using System.Text.Json.Nodes;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Runtime.Turn;
using AgentCore.Application.Scripting;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tools.Drawing;

/// <summary>
/// The inner tool the drawing agent calls. It is never declared in a document.
/// </summary>
/// <remarks>
/// The model hands over code, not a tree. The code runs over what this turn's tools answered, and
/// the tree it builds is what gets drawn. A model that types a hundred rows into a tree gets the
/// sums wrong and takes twenty seconds; a script that maps them takes two and gets them right.
/// </remarks>
internal static class PresentTool
{
    /// <summary>The name the drawing agent calls.</summary>
    internal const string Name = "present";

    /// <summary>The renderer name the browser looks up.</summary>
    internal const string RendererName = "generative-ui";

    /// <summary>Builds the inner tool for one declared drawing tool.</summary>
    /// <param name="toolId">The declared id, named in every error the model reads.</param>
    /// <param name="scripts">What runs the model's code.</param>
    internal static AIFunction Create(string toolId, IScriptRunnerPort scripts)
    {
        ArgumentNullException.ThrowIfNull(scripts);

        return AIFunctionFactory.Create(
            async ([Description("JavaScript: the body of a function that reads `data` and returns the tree to draw.")] string code,
                   CancellationToken cancellationToken)
                => await PublishAsync(toolId, scripts, code, cancellationToken).ConfigureAwait(false),
            new AIFunctionFactoryOptions
            {
                Name = Name,
                Description = "Run code that builds one tree for the caller and draw it. Call this once.",
                ExcludeResultSchema = true,
            });
    }

    private static async ValueTask<JsonObject> PublishAsync(
        string toolId, IScriptRunnerPort scripts, string code, CancellationToken cancellationToken)
    {
        if (CallRenderScope.Current is not { } screen)
        {
            return ToolErrorResult.Create(
                toolId, "this call has no screen, so nothing can be drawn on it. Say it in words instead.");
        }

        var data = TurnAmbients.Current?.Results?.Data() ?? new JsonObject { [TurnResults.AllKey] = new JsonObject() };
        var run = await scripts.RunAsync(new ScriptRequest(code, data) { Emit = Name }, cancellationToken).ConfigureAwait(false);

        if (run.Error is { } error)
        {
            return ToolErrorResult.Create(toolId, $"the script failed: {error} Fix it and call {Name} again.");
        }

        if (run.Value is not JsonObject node)
        {
            return ToolErrorResult.Create(
                toolId, $"the script did not return a tree. End it with `return` and one object with $type, then call {Name} again.");
        }

        try
        {
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
