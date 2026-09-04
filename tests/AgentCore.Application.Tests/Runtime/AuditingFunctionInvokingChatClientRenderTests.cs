using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Runtime;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tools.Builtin;
using AgentCore.Application.Tools.Drawing;
using AgentCore.Application.Tools.Shipped;
using AgentCore.Application.Transcript;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Runtime;

/// <summary>
/// <see cref="AuditingFunctionInvokingChatClient.CreateResponseMessages"/> attaches what a turn
/// drew to the tool-result message the drawing belongs to.
/// </summary>
/// <remarks>
/// Every test drives <see cref="AuditingFunctionInvokingChatClient"/> directly against a fake inner
/// <see cref="IChatClient"/>, exactly as <see cref="AuditingFunctionInvokingChatClientErrorPolicyTests"/>
/// does, with a <see cref="TurnRenders"/> opened as the ambient screen so a tool that draws has
/// somewhere to publish into. <see cref="DrawingAgentTests"/> already shows how to drive a nested
/// drawing agent; the outer-wins test below reuses that machinery for the nested half and drives the
/// outer call the same way this file's other tests do.
/// </remarks>
public sealed class AuditingFunctionInvokingChatClientRenderTests
{
    private static readonly ToolConfiguration DrawDeclaration = new()
    {
        Id = "draw",
        Kind = ToolKind.Builtin,
        Uses = BuiltinToolNames.Draw,
        Description = "Draw something for the caller.",
    };

    private const string Card = """
        { "$type": "Card", "children": [{ "$type": "Text", "children": ["hi"] }] }
        """;

    // ---------------------------------------------------------------------------------------
    // A streaming turn that draws once produces a tool-result message whose Contents hold one
    // FunctionResultContent and one RenderContent, in that order.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task AStreamingTurnThatDrawsOnce_AttachesOneRenderContentAfterTheFunctionResult()
    {
        var tool = DrawOnceTool();
        TurnRenders renders = new();
        using var scope = TurnAmbients.Amend(ambients => ambients with { Screen = renders, Renders = renders });

        ToolCallingChatClient inner = new("the loop continues.");
        using AuditingFunctionInvokingChatClient client = new(inner);
        ChatOptions options = new() { Tools = [tool] };

        List<ChatResponseUpdate> updates = [];
        await foreach (var update in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "draw it")], options, TestContext.Current.CancellationToken))
        {
            updates.Add(update);
        }

        var response = updates.ToChatResponse();
        var message = Assert.Single(response.Messages, m => m.Contents.Any(c => c is FunctionResultContent));

        Assert.Collection(
            message.Contents,
            content => Assert.IsType<FunctionResultContent>(content),
            content => Assert.IsType<RenderContent>(content));
    }

    // ---------------------------------------------------------------------------------------
    // D8: a tool that publishes the same render id twice leaves exactly one RenderContent on
    // the message, holding the second payload.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task PublishingTheSameRenderIdTwice_ReplacesTheEarlierOneInPlace()
    {
        var tool = AIFunctionFactory.Create(
            () =>
            {
                CallRenderScope.Current!.Publish("generative-ui", "chart-1", new { title = "loading" });
                CallRenderScope.Current!.Publish("generative-ui", "chart-1", new { title = "final" });
                return "drew.";
            },
            "build_chart",
            "Draw a chart for the caller.");

        TurnRenders renders = new();
        using var scope = TurnAmbients.Amend(ambients => ambients with { Screen = renders, Renders = renders });

        ToolCallingChatClient inner = new("the loop continues.");
        using AuditingFunctionInvokingChatClient client = new(inner);
        ChatOptions options = new() { Tools = [tool] };

        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "draw it")], options, TestContext.Current.CancellationToken);

        var drawn = response.Messages
            .SelectMany(m => m.Contents)
            .OfType<RenderContent>()
            .ToList();

        var render = Assert.Single(drawn);
        Assert.Equal("chart-1", render.RenderId);
        Assert.Equal("final", render.Data.GetProperty("title").GetString());
    }

    // ---------------------------------------------------------------------------------------
    // D9: a transient publish is dropped outright and leaves no RenderContent anywhere. There is
    // no live delivery path for one — see the remarks on IRenderPort.Publish's transient parameter.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task ATransientPublish_LeavesNoRenderContentAnywhere()
    {
        var tool = AIFunctionFactory.Create(
            () =>
            {
                CallRenderScope.Current!.Publish("generative-ui", "chart-1", new { title = "peek" }, transient: true);
                return "drew.";
            },
            "build_chart",
            "Draw a chart for the caller.");

        TurnRenders renders = new();
        using var scope = TurnAmbients.Amend(ambients => ambients with { Screen = renders, Renders = renders });

        ToolCallingChatClient inner = new("the loop continues.");
        using AuditingFunctionInvokingChatClient client = new(inner);
        ChatOptions options = new() { Tools = [tool] };

        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "draw it")], options, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(
            response.Messages.SelectMany(m => m.Contents),
            content => content is RenderContent);
    }

    // ---------------------------------------------------------------------------------------
    // The outer call id wins. A drawing made inside the nested drawing agent attaches to the
    // OUTER draw tool-result message, and not to the inner present call, which never reaches
    // the caller's own transcript at all. This is the guard for OuterToolCall's outermost-wins
    // rule.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task ADrawingMadeInsideTheNestedDrawingAgent_AttachesToTheOuterDrawMessage()
    {
        var drawTool = ShippedAgentBuilder.Build(
            new DrawingAgentDefinition(),
            DrawDeclaration,
            new BuiltinToolPorts(new RecordingChatClientFactory(new PresentCallingChatClient(Card))));

        TurnRenders renders = new();
        using var scope = TurnAmbients.Amend(ambients => ambients with { Screen = renders, Renders = renders });

        ToolCallingChatClient outer = new(
            "done.", new Dictionary<string, object?>(StringComparer.Ordinal) { ["query"] = "draw a card" });
        using AuditingFunctionInvokingChatClient client = new(outer);
        ChatOptions options = new() { Tools = [drawTool] };

        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "draw a card")], options, TestContext.Current.CancellationToken);

        var toolResultMessage = Assert.Single(
            response.Messages, m => m.Contents.Any(c => c is FunctionResultContent));

        Assert.Contains(toolResultMessage.Contents, content => content is RenderContent);
    }

    // ---------------------------------------------------------------------------------------
    // A nested loop's own tool call can mint the same id string as the outer call running it -
    // nothing about a vendor's id scheme forbids that. The outermost-wins guard on
    // CreateResponseMessages must still keep the nested loop from draining what belongs to the
    // outer call, or an id collision would attach the drawing to a message that never reaches
    // the caller.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task ANestedLoopWhoseOwnCallIdMatchesTheOuterOne_DoesNotStealTheDrawing()
    {
        var innerTool = AIFunctionFactory.Create(
            () =>
            {
                CallRenderScope.Current!.Publish("generative-ui", "chart-1", new { title = "Q3 revenue" });
                return "drew it.";
            },
            "inner_tool",
            "Draws for the caller.");

        var outerTool = AIFunctionFactory.Create(
            async () =>
            {
                // ToolCallingChatClient always hands out the same fixed call id, so running one as
                // the model behind a NESTED loop reproduces a vendor whose id scheme collides with
                // the outer call's own id, without needing a bespoke fake to force it.
                ToolCallingChatClient innerModel = new("nested done.");
                using AuditingFunctionInvokingChatClient innerClient = new(innerModel);
                ChatOptions innerOptions = new() { Tools = [innerTool] };
                await innerClient.GetResponseAsync(
                    [new ChatMessage(ChatRole.User, "draw")], innerOptions, TestContext.Current.CancellationToken);
                return "outer done.";
            },
            "outer_tool",
            "Runs a nested loop.");

        TurnRenders renders = new();
        using var scope = TurnAmbients.Amend(ambients => ambients with { Screen = renders, Renders = renders });

        ToolCallingChatClient outerModel = new("done.");
        using AuditingFunctionInvokingChatClient outerClient = new(outerModel);
        ChatOptions outerOptions = new() { Tools = [outerTool] };

        var response = await outerClient.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "go")], outerOptions, TestContext.Current.CancellationToken);

        var toolResultMessage = Assert.Single(
            response.Messages, m => m.Contents.Any(c => c is FunctionResultContent));

        Assert.Contains(toolResultMessage.Contents, content => content is RenderContent);
    }

    // ---------------------------------------------------------------------------------------
    // D8 (position): replacing an earlier render id must not move it. Publish a, then b, then a
    // again, and the order must stay [a, b] with a's payload updated - a remove-then-append
    // implementation would produce [b, a] instead.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public async Task PublishingUnderAnEarlierRenderId_KeepsItsOriginalPosition()
    {
        var tool = AIFunctionFactory.Create(
            () =>
            {
                var screen = CallRenderScope.Current!;
                screen.Publish("generative-ui", "a", new { title = "first" });
                screen.Publish("generative-ui", "b", new { title = "second" });
                screen.Publish("generative-ui", "a", new { title = "first-revised" });
                return "drew.";
            },
            "build_chart",
            "Draw a chart for the caller.");

        TurnRenders renders = new();
        using var scope = TurnAmbients.Amend(ambients => ambients with { Screen = renders, Renders = renders });

        ToolCallingChatClient inner = new("the loop continues.");
        using AuditingFunctionInvokingChatClient client = new(inner);
        ChatOptions options = new() { Tools = [tool] };

        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "draw it")], options, TestContext.Current.CancellationToken);

        var drawn = response.Messages
            .SelectMany(m => m.Contents)
            .OfType<RenderContent>()
            .ToList();

        Assert.Collection(
            drawn,
            first =>
            {
                Assert.Equal("a", first.RenderId);
                Assert.Equal("first-revised", first.Data.GetProperty("title").GetString());
            },
            second =>
            {
                Assert.Equal("b", second.RenderId);
                Assert.Equal("second", second.Data.GetProperty("title").GetString());
            });
    }

    // ---------------------------------------------------------------------------------------
    // TakeFor's publish-order contract: two distinct render ids under one call id come back in
    // the order they were published, not in whatever order a backing dictionary would iterate.
    // ---------------------------------------------------------------------------------------
    [Fact]
    public void TakeFor_ReturnsDistinctRenderIds_InPublishOrder()
    {
        TurnRenders renders = new();
        using var outer = OuterToolCall.Enter("call_1", out _);

        renders.Publish("generative-ui", "b", new { title = "second" });
        renders.Publish("generative-ui", "a", new { title = "first" });

        var drawn = renders.TakeFor("call_1");

        Assert.Collection(
            drawn,
            first => Assert.Equal("b", first.RenderId),
            second => Assert.Equal("a", second.RenderId));
    }

    /// <summary>A tool that draws once, unconditionally, and answers with words.</summary>
    private static AIFunction DrawOnceTool()
        => AIFunctionFactory.Create(
            () =>
            {
                CallRenderScope.Current!.Publish("generative-ui", "chart-1", new { title = "Q3 revenue" });
                return "drew a chart.";
            },
            "build_chart",
            "Draw a chart for the caller.");
}
