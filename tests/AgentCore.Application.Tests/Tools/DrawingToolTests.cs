using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.Application.Tools;
using AgentCore.Application.Tools.Builtin;
using AgentCore.Application.Tools.Drawing;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Tools;

/// <summary>
/// The <c>ui.draw</c> built-in: one string in, one line out, and the tree to the screen only.
/// </summary>
/// <remarks>
/// The point of the design is what the calling agent never sees. The 27-component vocabulary is
/// 19,355 bytes as a JSON Schema and would ride every request of every turn; here it is prose in a
/// second model's instructions, and the calling agent's tool is one string.
/// </remarks>
public sealed class DrawingToolTests
{
    private static readonly ToolConfiguration Declaration = new()
    {
        Id = "draw",
        Kind = ToolKind.Builtin,
        Uses = BuiltinToolNames.Draw,
        Description = "Draw something for the caller.",
    };

    [Fact]
    public void TheToolTheAgentSees_TakesOneStringAndCarriesNoVocabulary()
    {
        var tool = DrawingTool.Create(Declaration, static () => null);

        var schema = tool.JsonSchema.ToString();

        Assert.Equal("draw", tool.Name);
        Assert.Contains("request", schema, StringComparison.Ordinal);

        // The whole justification of this design. Every component name is in the instructions of a
        // private model call, and none of them is in what the calling agent is charged for.
        foreach (var component in DrawingTool.AllowedComponents)
        {
            Assert.DoesNotContain($"\"{component}\"", schema, StringComparison.Ordinal);
        }

        // Measured at 239. The bound is loose enough to survive a reworded description and tight
        // enough that anything structural landing here fails.
        Assert.True(schema.Length < 500, $"the advertised schema grew to {schema.Length} bytes.");
    }

    [Fact]
    public void TheAdvertisedSchema_IsATinyFractionOfTheShippedOne()
    {
        // 19,355 bytes is what buildPresentParameters(defaultGenerativeUILibrary) returns, measured
        // by running it against the pinned 0.0.15.
        const int ShippedSchemaBytes = 19_355;

        var schema = DrawingTool.Create(Declaration, static () => null).JsonSchema.ToString();

        Assert.True(
            schema.Length * 10 < ShippedSchemaBytes,
            $"the advertised schema is {schema.Length} bytes against the shipped {ShippedSchemaBytes}.");
    }

    [Fact]
    public async Task ATreeTheModelDraws_ReachesTheScreenUnchanged()
    {
        var tree = """
            {"$type":"Card","title":"Q3","children":[
              {"$type":"Chart","variant":"bar","showAxis":true,
               "data":[{"label":"Jul","value":22}]}]}
            """;

        var (result, screen) = await DrawAsync(tree);

        var published = Assert.Single(screen.Published);
        Assert.Equal("generative-ui", published.Name);
        Assert.Equal(
            JsonNode.Parse(tree)!.ToJsonString(),
            ((JsonObject)published.Data).ToJsonString());
        Assert.False(result.ContainsKey(ToolErrorResult.ErrorProperty));
    }

    [Fact]
    public async Task TheReceipt_NamesEveryButtonAndItsPayloadAndNothingElse()
    {
        var tree = """
            {"$type":"Card","title":"Approve?","children":[
              {"$type":"Button","label":"Yes","$action":{"type":"approve","id":42}},
              {"$type":"Button","label":"No","$action":{"type":"cancel","id":43}}]}
            """;

        var (result, _) = await DrawAsync(tree);

        var receipt = result["drew"]!.GetValue<string>();

        Assert.Contains("approve id=42", receipt, StringComparison.Ordinal);
        Assert.Contains("cancel id=43", receipt, StringComparison.Ordinal);

        // The tree itself must not ride back with it. This line is what the transcript and the
        // audit record keep forever.
        Assert.DoesNotContain("$type", receipt, StringComparison.Ordinal);
        Assert.DoesNotContain("Approve?", receipt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATreeWithNoButtons_SaysSoRatherThanLeavingItOpen()
    {
        var (result, _) = await DrawAsync("""{"$type":"Text","value":"hello"}""");

        Assert.Contains("buttons: none", result["drew"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AComponentOutsideTheVocabulary_IsRefusedAndTheModelIsToldWhat()
    {
        var client = new PresentingChatClient(
            """{"$type":"PieChart","value":"1"}""",
            """{"$type":"Text","value":"1"}""");

        var (result, screen) = await DrawAsync(client);

        // The bad tree never reaches the screen, and the good one does.
        var published = Assert.Single(screen.Published);
        Assert.Equal("Text", ((JsonObject)published.Data)["$type"]!.GetValue<string>());
        Assert.False(result.ContainsKey(ToolErrorResult.ErrorProperty));

        var told = Assert.Single(client.Prompts, text => text.Contains("PieChart", StringComparison.Ordinal));
        Assert.Contains("is not a component", told, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnActionOnAPropRatherThanOnTheNode_IsCheckedTheSameWay()
    {
        // vocabulary.md teaches Card.confirm/cancel as { "label", "$action" }, and the receipt names
        // every $action it can reach. A check that only walks children names a button it never read.
        var client = new PresentingChatClient(
            """{"$type":"Card","title":"Approve?","confirm":{"label":"Yes","$action":{"id":42}}}""",
            """{"$type":"Text","value":"1"}""");

        var (result, screen) = await DrawAsync(client);

        var published = Assert.Single(screen.Published);
        Assert.Equal("Text", ((JsonObject)published.Data)["$type"]!.GetValue<string>());
        Assert.False(result.ContainsKey(ToolErrorResult.ErrorProperty));

        var told = Assert.Single(client.Prompts, text => text.Contains("confirm", StringComparison.Ordinal));
        Assert.Contains("has no 'type'", told, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AModelThatNeverDrawsSomethingValid_GivesUpWithAnErrorReceipt()
    {
        var client = new PresentingChatClient(
            """{"$type":"Nope"}""",
            """{"$type":"Nope"}""",
            """{"$type":"Nope"}""",
            """{"$type":"Text","value":"too late"}""");

        var (result, screen) = await DrawAsync(client);

        Assert.True(result[ToolErrorResult.ErrorProperty]!.GetValue<bool>());
        Assert.Empty(screen.Published);

        // Bounded on purpose: section 8.7 budgets 40 rounds for the calling agent and this whole
        // tool is one of them.
        Assert.Equal(3, client.Calls);
    }

    [Fact]
    public async Task ACallWithNoScreen_SaysSoAndDrawsNothing()
    {
        // The voice path. A phone caller cannot be shown a chart, and the model must not tell them
        // they can see one.
        var tool = DrawingTool.Create(
            Declaration,
            () => new SingleChatClientFactory(new PresentingChatClient("""{"$type":"Text","value":"x"}""")));

        var result = Read(await tool.InvokeAsync(
            new AIFunctionArguments { ["request"] = "draw a chart" }, TestContext.Current.CancellationToken));

        Assert.True(result[ToolErrorResult.ErrorProperty]!.GetValue<bool>());
        Assert.Contains("no screen", result[ToolErrorResult.MessageProperty]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AModelThatThrows_IsAnErrorResultAndNotAnException()
    {
        // Section 8.7: a built-in answers the model and does not end the turn. The caller is on the
        // telephone.
        using var screen = RecordingRenderPort.Open(out var port);

        var tool = DrawingTool.Create(Declaration, () => new SingleChatClientFactory(new ThrowingChatClient()));

        var result = Read(await tool.InvokeAsync(
            new AIFunctionArguments { ["request"] = "draw" }, TestContext.Current.CancellationToken));

        Assert.True(result[ToolErrorResult.ErrorProperty]!.GetValue<bool>());
        Assert.Empty(port.Published);
    }

    [Fact]
    public async Task AnEmptyRequest_IsRefusedBeforeAnyModelIsCalled()
    {
        // Row T46: a model that omits an argument often sends the empty string instead.
        using var screen = RecordingRenderPort.Open(out var port);

        var client = new PresentingChatClient("""{"$type":"Text","value":"x"}""");
        var tool = DrawingTool.Create(Declaration, () => new SingleChatClientFactory(client));

        var result = Read(await tool.InvokeAsync(
            new AIFunctionArguments { ["request"] = "  " }, TestContext.Current.CancellationToken));

        Assert.True(result[ToolErrorResult.ErrorProperty]!.GetValue<bool>());
        Assert.Equal(0, client.Calls);
        Assert.Empty(port.Published);
    }

    [Fact]
    public void TheVocabulary_TeachesEveryComponentTheValidatorAllows()
    {
        var text = DrawingTool.VocabularyText;

        foreach (var component in DrawingTool.AllowedComponents)
        {
            Assert.Contains($"`{component}`", text, StringComparison.Ordinal);
        }

        Assert.Equal(27, DrawingTool.AllowedComponents.Length);
        Assert.Equal(DrawingTool.AllowedComponents.Length, DrawingTool.AllowedComponents.Distinct().Count());
    }

    /// <summary>Reads a tool result. The framework hands it back serialised, not as the object.</summary>
    private static JsonObject Read(object? result)
        => JsonSerializer.SerializeToNode(result)!.AsObject();

    private static async Task<(JsonObject Result, RecordingRenderPort Screen)> DrawAsync(string tree)
        => await DrawAsync(new PresentingChatClient(tree));

    private static async Task<(JsonObject Result, RecordingRenderPort Screen)> DrawAsync(PresentingChatClient client)
    {
        using var scope = RecordingRenderPort.Open(out var port);

        var tool = DrawingTool.Create(Declaration, () => new SingleChatClientFactory(client));

        var result = Read(await tool.InvokeAsync(
            new AIFunctionArguments { ["request"] = "draw the thing" },
            TestContext.Current.CancellationToken));

        return (result, port);
    }

    /// <summary>Records what a call draws, in the order it drew it.</summary>
    private sealed class RecordingRenderPort : IRenderPort
    {
        public List<(string Name, object Data)> Published { get; } = [];

        public static IDisposable Open(out RecordingRenderPort port)
        {
            port = new RecordingRenderPort();
            return CallRenderScope.Enter(port);
        }

        public void Publish(string name, object data) => Published.Add((name, data));
    }

    private sealed class SingleChatClientFactory : IChatClientFactory
    {
        private readonly IChatClient _client;

        public SingleChatClientFactory(IChatClient client) => _client = client;

        public IChatClient GetChatClient(ModelReference? model) => _client;
    }

    /// <summary>Answers each call with the next scripted tree, as a call to <c>present</c>.</summary>
    private sealed class PresentingChatClient : IChatClient
    {
        private readonly string[] _trees;
        private int _calls;

        public PresentingChatClient(params string[] trees) => _trees = trees;

        public int Calls => _calls;

        /// <summary>The last user message of each request, so a test reads what the model was told.</summary>
        public List<string> Prompts { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Prompts.Add(messages.Last().Text);

            var tree = _trees[Math.Min(_calls, _trees.Length - 1)];
            _calls++;

            ChatMessage reply = new(
                ChatRole.Assistant,
                [new FunctionCallContent(
                    Guid.NewGuid().ToString("N"),
                    "present",
                    new Dictionary<string, object?> { ["tree"] = JsonSerializer.Deserialize<JsonElement>(tree) })]);

            return Task.FromResult(new ChatResponse(reply));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            foreach (var update in response.ToChatResponseUpdates())
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
    }

    private sealed class ThrowingChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new HttpRequestException("the provider is down");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new HttpRequestException("the provider is down");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
