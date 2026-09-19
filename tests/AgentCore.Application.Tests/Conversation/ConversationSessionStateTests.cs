using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Conversation;
using Xunit;

namespace AgentCore.Application.Tests.Conversation;

public sealed class ConversationSessionStateTests
{
    [Fact]
    public void ItRoundTripsThroughJson()
    {
        ConversationSessionState state = new()
        {
            Stage = "collecting",
            IsComplete = false,
            Slots = new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
            {
                ["model"] = JsonValue.Create("F63"),
                ["serial"] = null,
                ["visits"] = JsonValue.Create(2),
                ["address"] = new JsonObject
                {
                    ["street"] = "1 Main St",
                    ["zip"] = JsonValue.Create(94107),
                },
                ["tags"] = new JsonArray(JsonValue.Create("urgent"), JsonValue.Create("repeat")),
            },
        };

        var json = JsonSerializer.Serialize(state, ConversationStateJson.Options);
        var read = JsonSerializer.Deserialize<ConversationSessionState>(json, ConversationStateJson.Options);

        Assert.NotNull(read);
        Assert.Equal(ConversationSessionState.CurrentVersion, read.Version);
        Assert.Equal("collecting", read.Stage);
        Assert.False(read.IsComplete);
        Assert.Equal("F63", read.Slots["model"]!.GetValue<string>());
        Assert.Null(read.Slots["serial"]);
        Assert.Equal(2, read.Slots["visits"]!.GetValue<int>());
        Assert.Equal("1 Main St", read.Slots["address"]!["street"]!.GetValue<string>());
        Assert.Equal(94107, read.Slots["address"]!["zip"]!.GetValue<int>());
        Assert.Equal(
            ["urgent", "repeat"],
            read.Slots["tags"]!.AsArray().Select(node => node!.GetValue<string>()));
    }

    [Fact]
    public void ANewStateCarriesTheCurrentVersion()
        => Assert.Equal(ConversationSessionState.CurrentVersion, new ConversationSessionState().Version);

    [Fact]
    public void ItRoundTripsProvidersThroughJson()
    {
        ConversationSessionState state = new()
        {
            Providers = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["TodoProvider"] = JsonDocument.Parse(
                    """{"items":[{"id":1,"title":"buy milk","isComplete":false}],"nextId":2}""").RootElement,
                ["AgentModeProvider"] = JsonDocument.Parse("""{"currentMode":"plan"}""").RootElement,
            },
        };

        var json = JsonSerializer.Serialize(state, ConversationStateJson.Options);
        var read = JsonSerializer.Deserialize<ConversationSessionState>(json, ConversationStateJson.Options);

        Assert.NotNull(read);
        Assert.Equal(2, read.Providers.Count);
        Assert.Equal(
            state.Providers["TodoProvider"].GetRawText(),
            read.Providers["TodoProvider"].GetRawText());
        Assert.Equal(
            state.Providers["AgentModeProvider"].GetRawText(),
            read.Providers["AgentModeProvider"].GetRawText());
    }

    [Fact]
    public void ABlobWithNoProvidersMember_DeserializesWithEmptyProviders()
    {
        var read = JsonSerializer.Deserialize<ConversationSessionState>(
            """{"version":1,"nextTurnIndex":0,"stage":"","isComplete":false}""", ConversationStateJson.Options);

        Assert.NotNull(read);
        Assert.Empty(read.Providers);
    }

    [Fact]
    public void ANewState_HasEmptyProviders()
        => Assert.Empty(new ConversationSessionState().Providers);
}
