using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentCore.Application.Calls;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.AspNetCore.Tests.Fakes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Endpoints;

/// <summary>
/// The whole wire a published file travels: the model calls <c>file.publish</c> on a file in the
/// call's workspace, the blob store keeps it under the call, the browser gets one
/// <c>agentcore_file</c> part with the link, and the transcript links the same file on a later read.
/// </summary>
public sealed class FileWireTests : IDisposable
{
    private const string CallId = "call-pub-1";

    private const string Yaml =
        """
          apiVersion: agentcore/v1
          tools:
            - { id: publish, kind: builtin, uses: file.publish }
          agents:
            defaults:
              model: { ref: reply }
            items:
              - { id: analyst, instructions: "hand over the report", tools: [ publish ] }
          entries:
            main:
              agent: analyst
          providers:
            call:   { kind: telnyx-relay }
            speech:
              stt: { kind: telnyx-relay }
              tts: { kind: telnyx-relay }
            llm:
              - { kind: openai, model: gpt-4.1-mini, as: reply }
            blobs: { kind: fake }
          """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "agentcore-filewire-" + Guid.NewGuid().ToString("N"));

    public FileWireTests() => Directory.CreateDirectory(Path.Combine(_root, CallId));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task AToolThatPublishes_ReachesTheBrowserAsAFilePartAndTheTranscriptLinksItLater()
    {
        File.WriteAllText(Path.Combine(_root, CallId, "report.csv"), "month,sales\njan,10\n");
        FakeBlobStoreAdapter blobs = new();

        await using var host = await ResponsesHost.StartAsync(
            Yaml,
            new PublishingChatClient(),
            configure: options => options.UseWorkspace(_root).UseBlobStores(blobs));

        using var response = await host.PostAsync(
            $$"""{ "stream": true, "conversation": "{{CallId}}", "input": "send me the report", "agentcore": { "message_id": "m1" } }""");
        var events = await ResponsesHost.ReadEventsAsync(response);

        var files = events
            .Select(text => JsonDocument.Parse(text).RootElement)
            .Where(chunk => chunk.TryGetProperty("agentcore_file", out var file) && file.ValueKind != JsonValueKind.Null)
            .Select(chunk => chunk.GetProperty("agentcore_file"))
            .ToList();

        var file = Assert.Single(files);
        Assert.Equal("report.csv", file.GetProperty("name").GetString());
        Assert.Equal("Quarterly report", file.GetProperty("title").GetString());
        Assert.Equal("text/csv", file.GetProperty("media_type").GetString());
        Assert.Equal(19, file.GetProperty("length").GetInt64());
        Assert.Equal($"https://blobs.test/{CallId}/report.csv?ttl=900", file.GetProperty("url").GetString());

        // The bytes are in the store, owned by the call.
        var (mediaType, bytes) = blobs.Store.Blobs[(CallId, "report.csv")];
        Assert.Equal("text/csv", mediaType);
        Assert.Equal("month,sales\njan,10\n", System.Text.Encoding.UTF8.GetString(bytes));

        // The model read the link back in its tool result.
        var toolResult = events
            .Select(text => JsonDocument.Parse(text).RootElement)
            .Where(chunk => chunk.TryGetProperty("agentcore_tool", out var tool)
                && tool.GetProperty("phase").GetString() == "result")
            .Select(chunk => chunk.GetProperty("agentcore_tool"))
            .Single();
        Assert.False(toolResult.GetProperty("failed").GetBoolean());
        Assert.Equal($"https://blobs.test/{CallId}/report.csv?ttl=900", toolResult.GetProperty("result").GetProperty("url").GetString());

        // And a later read of the transcript links the same file again.
        var calls = host.Services.GetRequiredService<CallRepository>();
        var stored = await calls.ReadAsync(CallId, TestContext.Current.CancellationToken);
        var links = await calls.LinkFilesAsync(CallId, stored.Select(row => row.Content), TestContext.Current.CancellationToken);

        var link = Assert.Single(links);
        Assert.Equal((CallId, "report.csv", "text/csv", 19L), (link.Blob.OwnerId, link.Blob.Name, link.Blob.MediaType, link.Blob.Length));
        Assert.Equal($"https://blobs.test/{CallId}/report.csv?ttl=900", link.Url?.ToString());
    }

    [Fact]
    public async Task AToolThatPublishesAMissingFile_TellsTheModelAndWritesNoFilePart()
    {
        FakeBlobStoreAdapter blobs = new();

        await using var host = await ResponsesHost.StartAsync(
            Yaml,
            new PublishingChatClient(),
            configure: options => options.UseWorkspace(_root).UseBlobStores(blobs));

        using var response = await host.PostAsync(
            $$"""{ "stream": true, "conversation": "{{CallId}}", "input": "send me the report", "agentcore": { "message_id": "m1" } }""");
        var events = await ResponsesHost.ReadEventsAsync(response);

        Assert.DoesNotContain(events, text => text.Contains("agentcore_file", StringComparison.Ordinal));

        var toolResult = events
            .Select(text => JsonDocument.Parse(text).RootElement)
            .Where(chunk => chunk.TryGetProperty("agentcore_tool", out var tool)
                && tool.GetProperty("phase").GetString() == "result")
            .Select(chunk => chunk.GetProperty("agentcore_tool"))
            .Single();
        Assert.True(toolResult.GetProperty("failed").GetBoolean());
        Assert.Contains("report.csv", toolResult.GetProperty("result").GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Empty(blobs.Store.Blobs);
    }

    [Fact]
    public async Task APublishToolWithNoBlobStore_FailsTheStartNamingTheToolAndThePort()
    {
        var yaml = Yaml.Replace("blobs: { kind: fake }", string.Empty, StringComparison.Ordinal);

        var failure = await Assert.ThrowsAsync<ConfigurationLoadException>(() => ResponsesHost.StartAsync(
            yaml,
            new PublishingChatClient(),
            configure: options => options.UseWorkspace(_root)));

        Assert.Contains("'publish'", failure.Message, StringComparison.Ordinal);
        Assert.Contains("providers.blobs", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>Calls the publish tool it is offered, once, on the report, then answers in words.</summary>
    private sealed class PublishingChatClient : IChatClient
    {
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();

            var alreadyPublished = messages.Any(message => message.Contents.OfType<FunctionResultContent>().Any());

            if (!alreadyPublished && options?.Tools?.OfType<AIFunction>().FirstOrDefault(tool => tool.Name == "publish") is { } publish)
            {
                yield return new ChatResponseUpdate(
                    ChatRole.Assistant,
                    [new FunctionCallContent(
                        "call_1",
                        publish.Name,
                        new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["path"] = "report.csv",
                            ["title"] = "Quarterly report",
                        })]);
                yield break;
            }

            yield return new ChatResponseUpdate(ChatRole.Assistant, "here is the report.");
        }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            List<ChatResponseUpdate> updates = [];
            await foreach (var update in GetStreamingResponseAsync(messages, options, cancellationToken).ConfigureAwait(false))
            {
                updates.Add(update);
            }

            return updates.ToChatResponse();
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
    }
}
