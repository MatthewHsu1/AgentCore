using System.Text.Json;
using AgentCore.Application.Transcript;
using AgentCore.Domain.Sources;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Transcript;

/// <summary>
/// A source is host-produced content that rides a message, exactly as a drawing does. It has to
/// survive the same serialiser, because the same message goes through the same store.
/// </summary>
public sealed class SourceContentTests
{
    [Fact]
    public void SourceContent_RoundTripsThroughTranscriptJson()
    {
        ChatMessage message = new(ChatRole.Assistant, [
            new SourceContent
            {
                Source = new SourceReference
                {
                    SourceId = "card-42",
                    Kind = SourceKind.Document,
                    Title = "Spirit CT900 owner's manual",
                    Origin = "knowledge",
                    Locator = "p.27",
                },
                CallId = "call-1",
            },
        ]);

        var json = JsonSerializer.Serialize(message, TranscriptJson.Options);
        var read = JsonSerializer.Deserialize<ChatMessage>(json, TranscriptJson.Options);

        var content = Assert.IsType<SourceContent>(Assert.Single(read!.Contents));
        Assert.Equal("call-1", content.CallId);
        Assert.Equal("card-42", content.Source.SourceId);
        Assert.Equal(SourceKind.Document, content.Source.Kind);
        Assert.Equal("Spirit CT900 owner's manual", content.Source.Title);
        Assert.Equal("knowledge", content.Source.Origin);
        Assert.Equal("p.27", content.Source.Locator);
        Assert.Null(content.Source.Url);
        Assert.Equal("text/plain", content.Source.MediaType);
    }

    [Fact]
    public void SourceReference_UrlKind_KeepsItsLink()
    {
        SourceReference source = new()
        {
            SourceId = "hit-1",
            Kind = SourceKind.Url,
            Title = "Spirit Fitness support",
            Origin = "web-search",
            Url = "https://example.com/support",
        };

        Assert.Equal(SourceKind.Url, source.Kind);
        Assert.Equal("https://example.com/support", source.Url);
        Assert.Equal(string.Empty, source.Locator);
    }
}
