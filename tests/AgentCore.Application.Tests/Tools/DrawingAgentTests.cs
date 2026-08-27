using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Runtime;
using AgentCore.Application.Tests.Runtime;
using AgentCore.Application.Tools;
using AgentCore.Application.Tools.Builtin;
using AgentCore.Application.Tools.Drawing;
using AgentCore.Application.Tools.Registry;
using AgentCore.Application.Tools.Shipped;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Tools;

/// <summary>
/// <c>ui.draw</c>: a shipped agent whose one inner tool is <c>present</c>.
/// </summary>
/// <remarks>
/// The point of the design is what the calling agent never sees. The 27-component vocabulary is
/// 19,355 bytes as a JSON Schema and would ride every request of every turn; here it is prose in the
/// drawing agent's instructions, and the calling agent's tool is one string. Retrying a tree that
/// does not validate belongs to the agent's own tool loop, so <see cref="PresentToolTests"/> owns
/// what one bad tree answers in isolation, and the retry itself is only covered here, end to end.
/// <see cref="ShippedAgentBuilderTests"/> owns the round cap as a mechanism, over a fake definition;
/// the cover for <see cref="DrawingAgentDefinition.DefaultMaxRounds"/> being three, and for what a
/// spent cap answers the calling agent, is here and nowhere else.
/// </remarks>
public sealed class DrawingAgentTests
{
    private static readonly ToolConfiguration Declaration = new()
    {
        Id = "draw",
        Kind = ToolKind.Builtin,
        Uses = BuiltinToolNames.Draw,
        Description = "Draw something for the caller.",
    };

    private const string Card = """
        { "$type": "Card", "children": [{ "$type": "Text", "children": ["hi"] }] }
        """;

    [Fact]
    public void TheToolTheAgentSees_TakesOneStringAndCarriesNoVocabulary()
    {
        var tool = Build(new RecordingChatClientFactory());

        var schema = tool.JsonSchema.ToString();

        Assert.Equal("draw", tool.Name);
        Assert.Contains("\"query\"", schema, StringComparison.Ordinal);

        // The whole justification of this design. Every component name is in the instructions of a
        // private agent, and none of them is in what the calling agent is charged for.
        foreach (var component in DrawingTree.AllowedComponents)
        {
            Assert.DoesNotContain($"\"{component}\"", schema, StringComparison.Ordinal);
        }

        // Measured at 128. The bound is loose enough to survive a reworded description and tight
        // enough that anything structural landing here fails.
        Assert.True(schema.Length < 500, $"the advertised schema grew to {schema.Length} bytes.");
    }

    [Fact]
    public void TheAdvertisedSchema_IsATinyFractionOfTheShippedOne()
    {
        // 19,355 bytes is what buildPresentParameters(defaultGenerativeUILibrary) returns, measured
        // by running it against the pinned 0.0.15.
        const int ShippedSchemaBytes = 19_355;

        var schema = Build(new RecordingChatClientFactory()).JsonSchema.ToString();

        Assert.True(
            schema.Length * 10 < ShippedSchemaBytes,
            $"the advertised schema is {schema.Length} bytes against the shipped {ShippedSchemaBytes}.");
    }

    [Fact]
    public async Task ADrawingAgent_ThatCallsPresentWithAValidTree_PublishesToTheScreen()
    {
        RecordingRenderPort screen = new();
        using var scope = TurnAmbients.Amend(ambients => ambients with { Screen = screen });

        var function = Build(new RecordingChatClientFactory(new PresentCallingChatClient(Card)));

        var result = await function.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["query"] = "draw a card" }),
            TestContext.Current.CancellationToken);

        var published = Assert.Single(screen.Published);
        Assert.Equal(PresentTool.RendererName, published.Name);
        Assert.Equal("Card", ((JsonObject)published.Data)["$type"]!.GetValue<string>());

        // What comes back to the calling agent is the drawing agent's final text and not the tree.
        Assert.Equal("drawn.", Assert.IsType<JsonElement>(result).GetString());
    }

    [Fact]
    public async Task ATreeTheValidatorRejects_ComesBackAsAnErrorAndTheAgentDrawsTheNextOne()
    {
        // The whole reason the hand-rolled retry loop could be deleted. Nothing in C# notices the
        // bad tree: present answers a section 8.7 error, the agent reads it, and asks again.
        RecordingRenderPort screen = new();
        using var scope = TurnAmbients.Amend(ambients => ambients with { Screen = screen });

        var result = await Draw("""{ "$type": "Wombat" }""", Card);

        var published = Assert.Single(screen.Published);
        Assert.Equal("Card", ((JsonObject)published.Data)["$type"]!.GetValue<string>());
        Assert.Equal("drawn.", Assert.IsType<JsonElement>(result).GetString());
    }

    /// <summary>
    /// Pins <see cref="DrawingAgentDefinition.DefaultMaxRounds"/> at three by what it does, not by
    /// reading the property back. Section 8.7 budgets 40 rounds for the calling agent and this whole
    /// tool is one of them, so the number is load-bearing and a change to it must fail here.
    /// </summary>
    [Theory]
    [InlineData(2, true)]
    [InlineData(3, false)]
    public async Task TheDefaultRoundCap_AllowsThreeTriesAndNoFourth(int rejected, bool drawn)
    {
        RecordingRenderPort screen = new();
        using var scope = TurnAmbients.Amend(ambients => ambients with { Screen = screen });

        string[] trees = [.. Enumerable.Repeat("""{ "$type": "Wombat" }""", rejected), Card];

        await Draw(trees);

        Assert.Equal(drawn, screen.Published.Count == 1);
    }

    [Fact]
    public async Task ADrawingAgentThatSpendsEveryRound_AnswersASection87ErrorAndNotAnEmptyString()
    {
        // AsAIFunction hands back the agent's final text, and the response the round cap stops on
        // holds only the tool call it refused to invoke, so the text is "". Handed that, the calling
        // agent would tell the caller their drawing is on screen.
        RecordingRenderPort screen = new();
        using var scope = TurnAmbients.Amend(ambients => ambients with { Screen = screen });

        var result = await Draw("""{ "$type": "Wombat" }""", """{ "$type": "Wombat" }""", """{ "$type": "Wombat" }""", Card);

        Assert.Empty(screen.Published);

        var error = Assert.IsType<JsonObject>(result);
        Assert.True(ToolErrorResult.IsError(error));
        Assert.Equal("draw", error[ToolErrorResult.ToolProperty]!.GetValue<string>());
        Assert.Contains("3", error[ToolErrorResult.MessageProperty]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSource_BuildsDraw_OnTheModelTheDocumentNames()
    {
        RecordingChatClientFactory factory = new();

        var registration = await Provide(Declaration with { Model = new ModelReference { Ref = "cheap" } }, factory);

        Assert.IsAssignableFrom<AIFunction>(registration.Materialise());
        Assert.Equal("cheap", factory.Asked!.Ref);
    }

    [Fact]
    public async Task TheSource_WithNoDeclaredDescription_TakesTheShippedDefaultOnBothSides()
    {
        var registration = await Provide(Declaration with { Description = null }, new RecordingChatClientFactory());
        var function = Assert.IsAssignableFrom<AIFunction>(registration.Materialise());

        // One string, resolved once: what the boot validates and what the model reads cannot differ.
        Assert.Equal(new DrawingAgentDefinition().DefaultDescription, registration.Description);
        Assert.Equal(registration.Description, function.Description);
    }

    [Fact]
    public async Task TheSource_WithNoChatClientFactory_FailsTheBoot()
    {
        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
            async () => await Provide(Declaration, factory: null));

        Assert.Contains("draw", failure.Message, StringComparison.Ordinal);
        Assert.Contains("IChatClientFactory", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AUsesNameNobodyShips_IsToldWhatIsShipped_IncludingTheShippedAgents()
    {
        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(
            async () => await Provide(Declaration with { Uses = "ui.drwa" }, new RecordingChatClientFactory()));

        Assert.Contains(BuiltinToolNames.Draw, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheReceipt_NamesEveryButtonAndItsPayloadAndNothingElse()
    {
        const string Tree = """
            {"$type":"Card","title":"Approve?","children":[
              {"$type":"Button","label":"Yes","$action":{"type":"approve","id":42}},
              {"$type":"Button","label":"No","$action":{"type":"cancel","id":43}}]}
            """;

        var receipt = DrawingReceipt.Describe(JsonNode.Parse(Tree)!.AsObject());

        Assert.Contains("approve id=42", receipt, StringComparison.Ordinal);
        Assert.Contains("cancel id=43", receipt, StringComparison.Ordinal);

        // The tree itself must not ride back with it. This line is what the transcript and the
        // audit record keep forever.
        Assert.DoesNotContain("$type", receipt, StringComparison.Ordinal);
        Assert.DoesNotContain("Approve?", receipt, StringComparison.Ordinal);
    }

    [Fact]
    public void ATreeWithNoButtons_SaysSoRatherThanLeavingItOpen()
    {
        var receipt = DrawingReceipt.Describe(JsonNode.Parse("""{"$type":"Text","value":"hello"}""")!.AsObject());

        Assert.Contains("buttons: none", receipt, StringComparison.Ordinal);
    }

    [Fact]
    public void TheVocabulary_TeachesEveryComponentTheValidatorAllows()
    {
        var text = DrawingVocabulary.Text;

        foreach (var component in DrawingTree.AllowedComponents)
        {
            Assert.Contains($"`{component}`", text, StringComparison.Ordinal);
        }

        Assert.Equal(27, DrawingTree.AllowedComponents.Length);
        Assert.Equal(DrawingTree.AllowedComponents.Length, DrawingTree.AllowedComponents.Distinct().Count());
    }

    /// <summary>Runs the drawing agent the document declares over one script of trees.</summary>
    private static async Task<object?> Draw(params string[] trees)
    {
        var registration = await Provide(
            Declaration, new RecordingChatClientFactory(new PresentCallingChatClient(trees)));

        var function = Assert.IsAssignableFrom<AIFunction>(registration.Materialise());

        return await function.InvokeAsync(
            new AIFunctionArguments(new Dictionary<string, object?> { ["query"] = "draw a card" }),
            TestContext.Current.CancellationToken);
    }

    private static AIFunction Build(RecordingChatClientFactory factory)
        => ShippedAgentBuilder.Build(
            new DrawingAgentDefinition(), Declaration, new BuiltinToolPorts(factory));

    /// <summary>Builds one declared tool through <see cref="BuiltinToolSource"/>.</summary>
    private static async Task<ToolRegistration> Provide(ToolConfiguration tool, RecordingChatClientFactory? factory)
    {
        BuiltinToolSource source = new(new BuiltinToolPorts(factory));
        var context = new ToolSourceContext(new AgentCoreConfiguration
        {
            ApiVersion = "agentcore/v1",
            Name = "test",
            Tools = [tool],
        });

        var registrations = await source.ProvideAsync(context, TestContext.Current.CancellationToken);

        return Assert.Single(registrations);
    }
}
