using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.State;
using AgentCore.Application.Tests.Fakes;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.State;

/// <summary>
/// The extractor of section 8.3. Every slot is nullable on the wire, and that is not optional.
/// </summary>
public sealed class StateExtractorTests
{
    private const string Yaml =
        """
        apiVersion: agentcore/v1
        name: extractor
        state:
          callerAskedForHuman: { type: boolean, default: false, writer: extractor }
          callerSaidGoodbye:   { type: boolean, default: false, writer: extractor }
          machineModel:        { type: string,  writer: extractor, description: the machine model }
          failedResolveTurns:  { type: integer, default: 0, writer: counter, increment: { var: callerSaidGoodbye } }
        extractor:
          model: { ref: fill }
          when: after_reply
        agents:
          items:
            - { id: only }
        """;

    private static readonly AgentCoreConfiguration Document = ConfigurationLoader.LoadYaml(Yaml);

    [Fact]
    public void TheSchema_HoldsOnlyTheExtractorSlots()
    {
        var schema = StateExtractor.BuildSchema(Document);

        var properties = schema.GetProperty("properties");
        Assert.Equal(3, properties.EnumerateObject().Count());
        Assert.False(properties.TryGetProperty("failedResolveTurns", out _));
    }

    [Fact]
    public void TheSchema_MakesEverySlotNullableAndRequired()
    {
        var schema = StateExtractor.BuildSchema(Document);

        foreach (var property in schema.GetProperty("properties").EnumerateObject())
        {
            var types = property.Value.GetProperty("type").EnumerateArray().Select(item => item.GetString()).ToList();

            // A missing field defaults silently, so every slot is nullable and every slot is required.
            Assert.Contains("null", types);
            Assert.Equal(2, types.Count);
        }

        var required = schema.GetProperty("required").EnumerateArray().Select(item => item.GetString()).ToList();
        Assert.Equal(3, required.Count);
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
    }

    [Fact]
    public void TheSchema_CarriesTheSlotDescription()
    {
        var schema = StateExtractor.BuildSchema(Document);

        Assert.Equal(
            "the machine model",
            schema.GetProperty("properties").GetProperty("machineModel").GetProperty("description").GetString());
    }

    [Fact]
    public void ANullField_LeavesThePreviousValue()
    {
        var (extractor, state) = Build();
        state.TryWrite("callerAskedForHuman", JsonValue.Create(true));

        var result = extractor.Write(state, """{ "callerAskedForHuman": null, "callerSaidGoodbye": null, "machineModel": null }""");

        Assert.True(result.Deserialized);
        Assert.Equal(3, result.LeftNull);
        Assert.Equal(0, result.Filled);
        Assert.True(state.Read("callerAskedForHuman")!.GetValue<bool>());
    }

    [Fact]
    public void AFalseField_WritesFalse()
    {
        var (extractor, state) = Build();
        state.TryWrite("callerAskedForHuman", JsonValue.Create(true));

        var result = extractor.Write(state, """{ "callerAskedForHuman": false, "callerSaidGoodbye": null, "machineModel": null }""");

        Assert.Equal(1, result.Filled);
        Assert.False(state.Read("callerAskedForHuman")!.GetValue<bool>());
        Assert.False(state.IsUnfilled("callerAskedForHuman"));
    }

    [Fact]
    public void UnfilledAndFilledFalse_AreDifferentStates()
    {
        var (extractor, state) = Build();

        // Both read as false. Only one of them has an answer behind it.
        Assert.True(state.IsUnfilled("callerSaidGoodbye"));
        Assert.False(state.Read("callerSaidGoodbye")!.GetValue<bool>());

        extractor.Write(state, """{ "callerSaidGoodbye": false }""");

        Assert.False(state.IsUnfilled("callerSaidGoodbye"));
        Assert.False(state.Read("callerSaidGoodbye")!.GetValue<bool>());
    }

    [Fact]
    public void AMissingField_IsTreatedAsNull()
    {
        var (extractor, state) = Build();
        state.TryWrite("machineModel", JsonValue.Create("F85"));

        var result = extractor.Write(state, """{ "callerSaidGoodbye": true }""");

        Assert.Equal(1, result.Filled);
        Assert.Equal(2, result.LeftNull);
        Assert.Equal("F85", state.Read("machineModel")!.GetValue<string>());
    }

    [Fact]
    public void AReplyThatDoesNotDeserialize_LeavesTheSlotsUnchanged()
    {
        var (extractor, state) = Build();
        state.TryWrite("machineModel", JsonValue.Create("F80"));

        var result = extractor.Write(state, "I am sorry, I cannot do that.");

        // Section 8.7: the extractor has no retry, and a failed extraction never drops a call.
        Assert.False(result.Deserialized);
        Assert.NotNull(result.Failure);
        Assert.Equal(0, result.Filled);
        Assert.Equal("F80", state.Read("machineModel")!.GetValue<string>());
    }

    [Fact]
    public void AnEmptyReply_LeavesTheSlotsUnchanged()
    {
        var (extractor, state) = Build();

        var result = extractor.Write(state, "");

        Assert.False(result.Deserialized);
        Assert.True(state.IsUnfilled("machineModel"));
    }

    [Fact]
    public void AnAnswerThatDoesNotCoerce_IsRejectedAndTheSlotStays()
    {
        var (extractor, state) = Build();

        var result = extractor.Write(state, """{ "machineModel": [ "F85" ] }""");

        Assert.Equal(1, result.Rejected);
        Assert.True(state.IsUnfilled("machineModel"));
    }

    [Fact]
    public async Task TheExtractor_MakesExactlyOneModelCall()
    {
        using ScriptedChatClient client = new("""{ "callerAskedForHuman": null, "callerSaidGoodbye": true, "machineModel": null }""");
        StateExtractor extractor = new(Document, client);
        StateDocument state = new(Document);

        var result = await extractor.ExtractAsync(
            state,
            [new ChatMessage(ChatRole.User, "goodbye")],
            TestContext.Current.CancellationToken);

        // The extractor has no retry: one call, and one only.
        Assert.Equal(1, client.Calls);
        Assert.True(result.Deserialized);
        Assert.True(state.Read("callerSaidGoodbye")!.GetValue<bool>());
        Assert.True(state.IsUnfilled("machineModel"));
    }

    [Fact]
    public async Task TheExtractor_SendsTheNullableSchemaAsTheResponseFormat()
    {
        using RecordingChatClient client = new("""{ "callerSaidGoodbye": null }""");
        StateExtractor extractor = new(Document, client);

        await extractor.ExtractAsync(
            new StateDocument(Document),
            [new ChatMessage(ChatRole.User, "hello")],
            TestContext.Current.CancellationToken);

        var format = Assert.IsType<ChatResponseFormatJson>(client.LastOptions!.ResponseFormat);
        Assert.Equal(StateExtractor.SchemaName, format.SchemaName);
        Assert.Contains("null", format.Schema!.Value.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public void ADocumentWithNoExtractorSection_Throws()
    {
        var document = ConfigurationLoader.LoadYaml(CompileTableYaml);
        using ScriptedChatClient client = new("{}");

        Assert.Throws<InvalidOperationException>(() => new StateExtractor(document, client));
    }

    private const string CompileTableYaml =
        """
        apiVersion: agentcore/v1
        name: no-extractor
        agents:
          items:
            - { id: only }
        """;

    private static (StateExtractor Extractor, StateDocument State) Build()
    {
        var client = new ScriptedChatClient("{}");
        return (new StateExtractor(Document, client), new StateDocument(Document));
    }

    private sealed class RecordingChatClient : IChatClient
    {
        private readonly string _reply;

        public RecordingChatClient(string reply) => _reply = reply;

        public ChatOptions? LastOptions { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            LastOptions = options;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _reply)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastOptions = options;
            yield return new ChatResponseUpdate(ChatRole.Assistant, _reply);
            await Task.CompletedTask.ConfigureAwait(false);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
            // Nothing to release.
        }
    }
}
