using System.Text.Json;
using AgentCore.AspNetCore.Endpoints;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Endpoints;

/// <summary>
/// The wire shape of <c>agentcore_data</c>: <c>{"name": ..., "data": ...}</c> and nothing else, no
/// matter what C# type on this side hands the serializer that value.
/// </summary>
public sealed class ChatCompletionContractsTests
{
    [Fact]
    public void AgentCoreData_SerializesAsNameAndDataOnly()
    {
        ChatCompletionResponse chunk = new()
        {
            Id = "chatcmpl-1",
            Object = "chat.completion.chunk",
            Created = 0,
            Model = "test",
            Choices = [new ChatCompletionChoice { Index = 0, Delta = new ChatCompletionMessage() }],
            AgentCoreData = new RenderedPayload
            {
                Name = "generative-ui",
                Data = JsonSerializer.SerializeToElement(new { title = "Q3 revenue" }),
            },
        };

        var json = JsonSerializer.Serialize(chunk, ChatCompletionJson.Options);
        var data = JsonDocument.Parse(json).RootElement.GetProperty("agentcore_data");

        Assert.Equal(
            """{"name":"generative-ui","data":{"title":"Q3 revenue"}}""",
            data.GetRawText());
    }

    [Fact]
    public void NoDrawing_LeavesAgentCoreDataAbsentRatherThanNull()
    {
        ChatCompletionResponse chunk = new()
        {
            Id = "chatcmpl-1",
            Object = "chat.completion.chunk",
            Created = 0,
            Model = "test",
            Choices = [new ChatCompletionChoice { Index = 0, Delta = new ChatCompletionMessage() }],
        };

        var json = JsonSerializer.Serialize(chunk, ChatCompletionJson.Options);

        Assert.DoesNotContain("agentcore_data", json, StringComparison.Ordinal);
    }

    [Fact]
    public void AParentIdTheBodyLeavesOut_IsNotTheSameAsOneItSendsAsNull()
    {
        var absent = JsonSerializer.Deserialize<ChatCompletionRequest>(
            """{"messages":[],"agentcore":{"message_id":"m1"}}""", ChatCompletionJson.Options);

        var explicitly = JsonSerializer.Deserialize<ChatCompletionRequest>(
            """{"messages":[],"agentcore":{"message_id":"m1","parent_id":null}}""",
            ChatCompletionJson.Options);

        // Both leave ParentId null, and the two mean opposite things: null names the start of the
        // call and withdraws every word of it. A client that forgot the member must not erase the
        // call it was adding to.
        Assert.Null(absent?.AgentCore?.ParentId);
        Assert.False(absent?.AgentCore?.NamesParent);

        Assert.Null(explicitly?.AgentCore?.ParentId);
        Assert.True(explicitly?.AgentCore?.NamesParent);
    }
}
