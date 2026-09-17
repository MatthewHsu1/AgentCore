#pragma warning disable OPENAI001
#pragma warning disable MEAI001 // IHostedFileClient is evaluation-only in Microsoft.Extensions.AI 10.10.0.

using AgentCore.Application.Transcript;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Runtime.CompilerServices;
using AgentCore.Application.Blobs;
using AgentCore.Infrastructure.Llm.OpenAI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Responses;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Llm;

/// <summary>
/// The vendor middleware that turns a <c>container_file_citation</c> into a <see cref="FileContent"/>.
/// </summary>
/// <remarks>
/// The wire shape is the OpenAI API reference's <c>container_file_citation</c> object. The
/// Microsoft.Extensions.AI.OpenAI adapter maps it to a <see cref="CitationAnnotation"/> and keeps
/// the SDK object on <see cref="AIAnnotation.RawRepresentation"/>; the container id lives only there.
/// </remarks>
public sealed class OpenAiSandboxFilesClientTests
{
    private const string CitationJson = """
        {
          "type": "container_file_citation",
          "container_id": "cntr_68c8f1a2b3c4d5e6f7a8b9c0",
          "file_id": "cfile_68c8f1a2b3c4d5e6f7a8b9c1",
          "filename": "chart.png",
          "start_index": 42,
          "end_index": 71
        }
        """;

    private static readonly OpenAIClient OpenAi = new(new ApiKeyCredential("sk-test"));

    [Fact]
    public async Task GetResponseAsync_ContainerFileCitation_AddsOneFileContent()
    {
        // Arrange
        var inner = new ScriptedChatClient(new ChatResponse(new ChatMessage(ChatRole.Assistant, [CitedText()])));
        using var client = new OpenAiSandboxFilesClient(inner, OpenAi);

        // Act
        var response = await client.GetResponseAsync("draw", cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        var file = Assert.Single(response.Messages[0].Contents.OfType<FileContent>());
        Assert.Equal("cfile_68c8f1a2b3c4d5e6f7a8b9c1", file.FileId);
        Assert.Equal("cntr_68c8f1a2b3c4d5e6f7a8b9c0", file.Scope);
        Assert.Equal("chart.png", file.Name);
        Assert.False(file.Kept);
        Assert.Equal("Here is your chart: [Download](sandbox:/mnt/data/chart.png)", response.Text);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_ContainerFileCitation_AddsItToThatUpdate()
    {
        // Arrange
        var inner = new ScriptedChatClient(new ChatResponse(new ChatMessage(ChatRole.Assistant, [CitedText()])));
        using var client = new OpenAiSandboxFilesClient(inner, OpenAi);

        // Act
        var files = new List<FileContent>();
        await foreach (var update in client.GetStreamingResponseAsync("draw", cancellationToken: TestContext.Current.CancellationToken))
        {
            files.AddRange(update.Contents.OfType<FileContent>());
        }

        // Assert
        Assert.Equal("cfile_68c8f1a2b3c4d5e6f7a8b9c1", Assert.Single(files).FileId);
    }

    [Fact]
    public async Task GetResponseAsync_CitationWithoutTheRawObject_AddsNothing()
    {
        // Arrange: a citation from some other source (a vector-store file, say) carries no container.
        TextContent text = new("see [1]")
        {
            Annotations = [new CitationAnnotation { FileId = "file-abc", Title = "notes.txt" }],
        };
        var inner = new ScriptedChatClient(new ChatResponse(new ChatMessage(ChatRole.Assistant, [text])));
        using var client = new OpenAiSandboxFilesClient(inner, OpenAi);

        // Act
        var response = await client.GetResponseAsync("cite", cancellationToken: TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(response.Messages[0].Contents.OfType<FileContent>());
    }

    [Fact]
    public void GetService_IHostedFileClient_AnswersThroughTheWrappersAbove()
    {
        // Arrange: the same wrappers the composite and the harness put above the vendor client.
        using var client = new OpenAiSandboxFilesClient(new ScriptedChatClient(new ChatResponse()), OpenAi)
            .AsBuilder()
            .ConfigureOptions(options => options.Temperature ??= 0.2f)
            .UseFunctionInvocation()
            .Build();

        // Act & Assert
        Assert.NotNull(client.GetService<IHostedFileClient>());
    }

    [Fact]
    public void GetService_IHostedFileClient_TheVendorClientAloneAnswersNothing()
    {
        // The reason the middleware exists.
        using var vendor = OpenAi.GetResponsesClient().AsIChatClient("gpt-x");

        Assert.Null(vendor.GetService<IHostedFileClient>());
    }

    private static TextContent CitedText()
    {
        var raw = ModelReaderWriter.Read<ContainerFileCitationMessageAnnotation>(BinaryData.FromString(CitationJson))!;

        return new TextContent("Here is your chart: [Download](sandbox:/mnt/data/chart.png)")
        {
            Annotations = [new CitationAnnotation { FileId = raw.FileId, Title = raw.Filename, RawRepresentation = raw }],
        };
    }

    /// <summary>An inner client that answers one fixed response, whole or as one update per message.</summary>
    private sealed class ScriptedChatClient(ChatResponse response) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult(response);

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var message in response.Messages)
            {
                yield return new ChatResponseUpdate(message.Role, [.. message.Contents]);
            }

            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
