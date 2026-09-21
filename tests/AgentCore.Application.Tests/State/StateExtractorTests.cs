using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.State;
using AgentCore.Application.Tests.Fakes;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.State
{
    /// <summary>
    /// The extractor of section 8.3. Every slot is nullable on the wire, and that is not optional.
    /// </summary>
    public sealed class StateExtractorTests
    {
        private const string Yaml =
            """
        apiVersion: agentcore/v1
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
        entries:
          main:
            agent: only
        """;

        private static readonly AgentCoreConfiguration Document = ConfigurationLoader.LoadYaml(Yaml);

        [Fact]
        public void TheSchema_HoldsOnlyTheExtractorSlots()
        {
            JsonElement schema = StateExtractor.BuildSchema(Document);

            JsonElement properties = schema.GetProperty("properties");
            Assert.Equal(3, properties.EnumerateObject().Count());
            Assert.False(properties.TryGetProperty("failedResolveTurns", out _));
        }

        [Fact]
        public void TheSchema_MakesEverySlotNullableAndRequired()
        {
            JsonElement schema = StateExtractor.BuildSchema(Document);

            foreach (JsonProperty property in schema.GetProperty("properties").EnumerateObject())
            {
                List<string?> types = [.. property.Value.GetProperty("type").EnumerateArray().Select(item => item.GetString())];

                // A missing field defaults silently, so every slot is nullable and every slot is required.
                Assert.Contains("null", types);
                Assert.Equal(2, types.Count);
            }

            List<string?> required = [.. schema.GetProperty("required").EnumerateArray().Select(item => item.GetString())];
            Assert.Equal(3, required.Count);
            Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        }

        [Fact]
        public void TheSchema_CarriesTheSlotDescription()
        {
            JsonElement schema = StateExtractor.BuildSchema(Document);

            Assert.Equal(
                "the machine model",
                schema.GetProperty("properties").GetProperty("machineModel").GetProperty("description").GetString());
        }

        [Fact]
        public void ANullField_LeavesThePreviousValue()
        {
            (StateExtractor? extractor, StateDocument? state) = Build();
            _ = state.TryWrite("callerAskedForHuman", JsonValue.Create(true));

            StateExtractionResult result = extractor.Write(state, /*lang=json,strict*/ """{ "callerAskedForHuman": null, "callerSaidGoodbye": null, "machineModel": null }""");

            Assert.True(result.Deserialized);
            Assert.Equal(3, result.LeftNull);
            Assert.Equal(0, result.Filled);
            Assert.True(state.Read("callerAskedForHuman")!.GetValue<bool>());
        }

        [Fact]
        public void AFalseField_WritesFalse()
        {
            (StateExtractor? extractor, StateDocument? state) = Build();
            _ = state.TryWrite("callerAskedForHuman", JsonValue.Create(true));

            StateExtractionResult result = extractor.Write(state, /*lang=json,strict*/ """{ "callerAskedForHuman": false, "callerSaidGoodbye": null, "machineModel": null }""");

            Assert.Equal(1, result.Filled);
            Assert.False(state.Read("callerAskedForHuman")!.GetValue<bool>());
            Assert.False(state.IsUnfilled("callerAskedForHuman"));
        }

        [Fact]
        public void UnfilledAndFilledFalse_AreDifferentStates()
        {
            (StateExtractor? extractor, StateDocument? state) = Build();

            // Both read as false. Only one of them has an answer behind it.
            Assert.True(state.IsUnfilled("callerSaidGoodbye"));
            Assert.False(state.Read("callerSaidGoodbye")!.GetValue<bool>());

            _ = extractor.Write(state, /*lang=json,strict*/ """{ "callerSaidGoodbye": false }""");

            Assert.False(state.IsUnfilled("callerSaidGoodbye"));
            Assert.False(state.Read("callerSaidGoodbye")!.GetValue<bool>());
        }

        [Fact]
        public void AMissingField_IsTreatedAsNull()
        {
            (StateExtractor? extractor, StateDocument? state) = Build();
            _ = state.TryWrite("machineModel", JsonValue.Create("F85"));

            StateExtractionResult result = extractor.Write(state, /*lang=json,strict*/ """{ "callerSaidGoodbye": true }""");

            Assert.Equal(1, result.Filled);
            Assert.Equal(2, result.LeftNull);
            Assert.Equal("F85", state.Read("machineModel")!.GetValue<string>());
        }

        [Fact]
        public void AReplyThatDoesNotDeserialize_LeavesTheSlotsUnchanged()
        {
            (StateExtractor? extractor, StateDocument? state) = Build();
            _ = state.TryWrite("machineModel", JsonValue.Create("F80"));

            StateExtractionResult result = extractor.Write(state, "I am sorry, I cannot do that.");

            // Section 8.7: the extractor has no retry, and a failed extraction never drops a conversation.
            Assert.False(result.Deserialized);
            Assert.NotNull(result.Failure);
            Assert.Equal(0, result.Filled);
            Assert.Equal("F80", state.Read("machineModel")!.GetValue<string>());
        }

        [Fact]
        public void AnEmptyReply_LeavesTheSlotsUnchanged()
        {
            (StateExtractor? extractor, StateDocument? state) = Build();

            StateExtractionResult result = extractor.Write(state, "");

            Assert.False(result.Deserialized);
            Assert.True(state.IsUnfilled("machineModel"));
        }

        [Fact]
        public void AnAnswerThatDoesNotCoerce_IsRejectedAndTheSlotStays()
        {
            (StateExtractor? extractor, StateDocument? state) = Build();

            StateExtractionResult result = extractor.Write(state, /*lang=json,strict*/ """{ "machineModel": [ "F85" ] }""");

            Assert.Equal(1, result.Rejected);
            Assert.True(state.IsUnfilled("machineModel"));
        }

        [Fact]
        public async Task TheExtractor_MakesExactlyOneModelCall()
        {
            using ScriptedChatClient client = new(/*lang=json,strict*/ """{ "callerAskedForHuman": null, "callerSaidGoodbye": true, "machineModel": null }""");
            StateExtractor extractor = new(Document, client);
            StateDocument state = new(Document);

            StateExtractionResult result = await extractor.ExtractAsync(
                state,
                [new ChatMessage(ChatRole.User, "goodbye")],
                TestContext.Current.CancellationToken);

            // The extractor has no retry: one conversation, and one only.
            Assert.Equal(1, client.Calls);
            Assert.True(result.Deserialized);
            Assert.True(state.Read("callerSaidGoodbye")!.GetValue<bool>());
            Assert.True(state.IsUnfilled("machineModel"));
        }

        [Fact]
        public async Task TheExtractor_SendsTheNullableSchemaAsTheResponseFormat()
        {
            using RecordingChatClient client = new(/*lang=json,strict*/ """{ "callerSaidGoodbye": null }""");
            StateExtractor extractor = new(Document, client);

            _ = await extractor.ExtractAsync(
                new StateDocument(Document),
                [new ChatMessage(ChatRole.User, "hello")],
                TestContext.Current.CancellationToken);

            ChatResponseFormatJson format = Assert.IsType<ChatResponseFormatJson>(client.LastOptions!.ResponseFormat);
            Assert.Equal(StateExtractor.SchemaName, format.SchemaName);
            Assert.Contains("null", format.Schema!.Value.GetRawText(), StringComparison.Ordinal);
        }

        [Fact]
        public void ADocumentWithNoExtractorSection_Throws()
        {
            AgentCoreConfiguration document = ConfigurationLoader.LoadYaml(CompileTableYaml);
            using ScriptedChatClient client = new("{}");

            _ = Assert.Throws<InvalidOperationException>(() => new StateExtractor(document, client));
        }

        private const string CompileTableYaml =
            """
        apiVersion: agentcore/v1
        agents:
          items:
            - { id: only }
        entries:
          main:
            agent: only
        """;

        private static (StateExtractor Extractor, StateDocument State) Build()
        {
            ScriptedChatClient client = new("{}");
            return (new StateExtractor(Document, client), new StateDocument(Document));
        }

        private sealed class RecordingChatClient(string reply) : IChatClient
        {
            private readonly string _reply = reply;

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

            public object? GetService(Type serviceType, object? serviceKey = null)
            {
                return null;
            }

            public void Dispose()
            {
                // Nothing to release.
            }
        }
    }
}
