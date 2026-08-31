using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Transcript;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Transcript;

/// <summary>
/// Pins that <see cref="AgentCoreChatHistoryProvider"/> reads its transcript back out of the session's
/// state bag under <see cref="TranscriptJson.Options"/>, not <c>AIJsonUtilities.DefaultOptions</c>.
/// <see cref="CallTranscript.Append"/> strips every <see cref="RenderContent"/> before a message reaches
/// <see cref="CallTranscript.Messages"/>, so the provider can no longer write one into the bag itself —
/// but a transcript written before that change, or by some other writer, still can carry one, and
/// nothing else in the suite would catch the provider quietly losing the options that let it come back.
/// </summary>
public sealed class AgentCoreChatHistoryProviderStateBagTests
{
    private const string CallId = "call-1";
    private const string StateKey = "AgentCoreChatHistoryProvider";

    /// <summary>
    /// Seeds a session's state bag directly with a transcript carrying a <see cref="RenderContent"/> —
    /// standing in for one written before <see cref="CallTranscript.Append"/> started stripping, or by a
    /// writer other than this provider — then drives the provider's own read path over it. This is the
    /// regression this test exists to catch: revert the provider's constructor from
    /// <see cref="TranscriptJson.Options"/> back to <c>AIJsonUtilities.DefaultOptions</c> and this throws,
    /// because that options set has no converter for the "agentcore.render" discriminator.
    /// </summary>
    [Fact]
    public void Read_ATranscriptWrittenWithARenderContent_ComesBackIntact()
    {
        // Arrange
        var seed = new AgentSessionStateBag();
        seed.SetValue(StateKey, TranscriptWithARenderContent(out var data), TranscriptJson.Options);

        // Round-tripped through Serialize/Deserialize so the bag holds raw JSON, the way a session
        // loaded from storage would: a same-process SetValue keeps the CLR object and never touches
        // these options at all, which would let this test pass no matter what the provider is wired to.
        var session = new StubSession(AgentSessionStateBag.Deserialize(seed.Serialize()));
        var provider = new AgentCoreChatHistoryProvider(new RecordingCallStore());

        // Act
        var read = provider.Read(session);

        // Assert
        var render = Assert.Single(Assert.Single(read).Contents.OfType<RenderContent>());
        Assert.Equal("order-card", render.Name);
        Assert.Equal("order-41", render.RenderId);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(data.GetRawText()), JsonNode.Parse(render.Data.GetRawText())));
    }

    /// <summary>
    /// A drawing carried on a stored message is unreadable under the options the provider used before
    /// this fix: <c>AIJsonUtilities.DefaultOptions</c> has no converter for the "agentcore.render"
    /// discriminator, so deserializing it throws rather than quietly dropping the drawing.
    /// </summary>
    [Fact]
    public void DefaultOptions_ReadingARenderContent_ThrowsInsteadOfDroppingIt()
    {
        // Arrange
        var bag = new AgentSessionStateBag();
        bag.SetValue(StateKey, TranscriptWithARenderContent(out _), TranscriptJson.Options);
        var deserialized = AgentSessionStateBag.Deserialize(bag.Serialize());

        // Act & Assert
        var exception = Assert.Throws<JsonException>(
            () => deserialized.TryGetValue<CallTranscript>(StateKey, out _, AIJsonUtilities.DefaultOptions));
        Assert.Contains("agentcore.render", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Builds a transcript carrying a <see cref="RenderContent"/> directly, bypassing
    /// <see cref="CallTranscript.Append"/>: that strips the <see cref="RenderContent"/> before it
    /// reaches <see cref="CallTranscript.Messages"/>, which is exactly what these tests need to get
    /// past to exercise the read side.
    /// </summary>
    private static CallTranscript TranscriptWithARenderContent(out JsonElement data)
    {
        data = JsonSerializer.SerializeToElement(new { widget = "gauge" });

        var transcript = new CallTranscript { CallId = CallId };
        transcript.Messages.Add(new CallTranscript.StoredMessage
        {
            Ordinal = 0,
            TurnIndex = 0,
            Message = new ChatMessage(ChatRole.Assistant,
            [
                new FunctionResultContent("call-1", new { status = "shipped" }),
                new RenderContent { Name = "order-card", RenderId = "order-41", Data = data },
            ]),
        });

        return transcript;
    }

    private sealed class StubSession(AgentSessionStateBag bag) : AgentSession(bag);
}
