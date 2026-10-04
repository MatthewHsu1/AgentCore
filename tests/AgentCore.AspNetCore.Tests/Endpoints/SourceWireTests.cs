using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentCore.Domain.Sources;
using AgentCore.AspNetCore.Tests.Fakes;
using Microsoft.Extensions.AI;
using Xunit;
using AgentCore.Application.Runtime.Turn;

namespace AgentCore.AspNetCore.Tests.Endpoints
{
    /// <summary>
    /// The whole wire a source travels: a producer, the conversation's sources, and one extra SSE field.
    /// </summary>
    public sealed class SourceWireTests
    {
        private const string SourceYaml =
            """
          apiVersion: agentcore/v1
          tools:
            - id: look_it_up
              kind: binding
              binds: LookItUp
              description: Look something up for the caller.
          agents:
            defaults:
              model: { ref: reply }
            items:
              - { id: greeter, instructions: "greet the caller", tools: [ look_it_up ] }
              - { id: closer,  instructions: "close the conversation",   tools: [ look_it_up ] }
          entries:
            main:
              policy:
                initial: greeting
                stages:
                  - { id: greeting, agent: greeter, to: [ { stage: close } ] }
                  - { id: close,    agent: closer,  to: [ { stage: greeting } ] }
          providers:
            conversation:   { kind: telnyx-relay }
            speech:
              stt: { kind: telnyx-relay }
              tts: { kind: telnyx-relay }
            llm:
              - { kind: openai, model: gpt-4.1-mini, as: reply }
          """;

        [Fact]
        public async Task AToolThatCites_ReachesTheBrowserOnItsOwnFieldAndNotInTheReply()
        {
            await using ResponsesHost host = await ResponsesHost.StartAsync(
                SourceYaml,
                new SourceCitingChatClient(),
                configure: options => options.Bind("LookItUp", (TurnInvocation? turn) =>
                {
                    turn?.Sources?.Publish(new SourceReference
                    {
                        SourceId = "card-42",
                        Kind = SourceKind.Document,
                        Title = "Spirit CT900 owner's manual",
                        Origin = "knowledge",
                        Locator = "p.27",
                    }, turn.OuterCallId);

                    return ValueTask.FromResult<object?>("E03 is an overcurrent trip.");
                }));

            using HttpResponseMessage response = await PostStreamAsync(host, "what is E03");
            List<string> events = await ResponsesHost.ReadEventsAsync(response);

            List<JsonElement> cited = [.. events
                .Select(text => JsonDocument.Parse(text).RootElement)
                .Where(chunk => chunk.TryGetProperty("agentcore_source", out JsonElement source)
                    && source.ValueKind != JsonValueKind.Null)];

            JsonElement chunk = Assert.Single(cited).GetProperty("agentcore_source");

            Assert.Equal("card-42", chunk.GetProperty("id").GetString());
            Assert.Equal("document", chunk.GetProperty("source_type").GetString());
            Assert.Equal("Spirit CT900 owner's manual", chunk.GetProperty("title").GetString());
            Assert.Equal("p.27", chunk.GetProperty("locator").GetString());
            Assert.Equal("knowledge", chunk.GetProperty("origin").GetString());
            Assert.False(string.IsNullOrEmpty(chunk.GetProperty("call_id").GetString()));

            // And it is not in what the caller is told. The spoken reply is what the transcript keeps.
            string spoken = string.Concat(ResponsesHost.TextDeltas(events));

            Assert.DoesNotContain("card-42", spoken, StringComparison.Ordinal);
        }

        [Fact]
        public async Task AToolThatCitesNothing_WritesNoSourceField()
        {
            await using ResponsesHost host = await ResponsesHost.StartAsync(
                SourceYaml,
                new SourceCitingChatClient(),
                configure: options => options.Bind("LookItUp", (_, _) =>
                    ValueTask.FromResult<object?>("nothing to cite")));

            using HttpResponseMessage response = await PostStreamAsync(host, "what is E03");
            List<string> events = await ResponsesHost.ReadEventsAsync(response);

            Assert.DoesNotContain(events, text => text.Contains("agentcore_source", StringComparison.Ordinal));
        }

        [Fact]
        public async Task AToolThatCitesUnderTwoParallelCalls_StampsEachSourceWithItsOwnCallId()
        {
            // FunctionInvokingChatClient batches every parallel call's results of one round onto ONE
            // message (and this endpoint turns that message into ONE update), so a fix that reads the
            // call id off "the first FunctionResultContent on the update" rather than off the source
            // itself would silently stamp the second source with the first conversation's id. This is the
            // regression test for exactly that.
            await using ResponsesHost host = await ResponsesHost.StartAsync(
                SourceYaml,
                new TwoParallelConversationsChatClient(),
                configure: options => options.Bind("LookItUp", (string? what, TurnInvocation? turn) =>
                {
                    turn?.Sources?.Publish(new SourceReference
                    {
                        SourceId = what == "left" ? "card-left" : "card-right",
                        Kind = SourceKind.Document,
                        Title = what == "left" ? "Left manual" : "Right manual",
                        Origin = "knowledge",
                    }, turn.OuterCallId);

                    return ValueTask.FromResult<object?>("looked up " + what);
                }));

            using HttpResponseMessage response = await PostStreamAsync(host, "what is E03");
            List<string> events = await ResponsesHost.ReadEventsAsync(response);

            List<JsonElement> cited = [.. events
                .Select(text => JsonDocument.Parse(text).RootElement)
                .Where(chunk => chunk.TryGetProperty("agentcore_source", out JsonElement source)
                    && source.ValueKind != JsonValueKind.Null)
                .Select(chunk => chunk.GetProperty("agentcore_source"))];

            Assert.Equal(2, cited.Count);

            JsonElement left = Assert.Single(cited, source => source.GetProperty("id").GetString() == "card-left");
            JsonElement right = Assert.Single(cited, source => source.GetProperty("id").GetString() == "card-right");

            Assert.Equal("conversation_1", left.GetProperty("call_id").GetString());
            Assert.Equal("conversation_2", right.GetProperty("call_id").GetString());
        }

        /// <summary>Sends one turn of words and reads the answer as it arrives.</summary>
        private static Task<HttpResponseMessage> PostStreamAsync(ResponsesHost host, string text, string? conversation = null)
        {
            return host.PostAsync(conversation is { Length: > 0 }
                        ? $$"""{ "stream": true, "conversation": "{{conversation}}", "input": "{{text}}", "agentcore": { "message_id": "m1" } }"""
                        : $$"""{ "stream": true, "input": "{{text}}", "agentcore": { "message_id": "m1" } }""");
        }

        /// <summary>Calls the first tool it is offered, once, then answers in words.</summary>
        private sealed class SourceCitingChatClient : IChatClient
        {
            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                await Task.Yield();

                bool alreadyCited = messages.Any(message => message.Contents.OfType<FunctionResultContent>().Any());

                if (!alreadyCited && options?.Tools?.OfType<AIFunction>().FirstOrDefault() is { } tool)
                {
                    yield return new ChatResponseUpdate(
                        ChatRole.Assistant,
                        [new FunctionCallContent(
                              "conversation_1",
                              tool.Name,
                              new Dictionary<string, object?>(StringComparer.Ordinal) { ["what"] = "E03" })]);
                    yield break;
                }

                yield return new ChatResponseUpdate(ChatRole.Assistant, "it is an overcurrent trip.");
            }

            public async Task<ChatResponse> GetResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                CancellationToken cancellationToken = default)
            {
                List<ChatResponseUpdate> updates = [];
                await foreach (ChatResponseUpdate? update in GetStreamingResponseAsync(messages, options, cancellationToken)
                    .ConfigureAwait(false))
                {
                    updates.Add(update);
                }

                return updates.ToChatResponse();
            }

            public object? GetService(Type serviceType, object? serviceKey = null)
            {
                return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
            }

            public void Dispose()
            {
            }
        }

        /// <summary>Calls the tool it is offered twice in one round — two parallel calls — then answers in words.</summary>
        private sealed class TwoParallelConversationsChatClient : IChatClient
        {
            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                await Task.Yield();

                bool alreadyCited = messages.Any(message => message.Contents.OfType<FunctionResultContent>().Any());

                if (!alreadyCited && options?.Tools?.OfType<AIFunction>().FirstOrDefault() is { } tool)
                {
                    yield return new ChatResponseUpdate(
                        ChatRole.Assistant,
                        [
                            new FunctionCallContent(
                                  "conversation_1",
                                  tool.Name,
                                  new Dictionary<string, object?>(StringComparer.Ordinal) { ["what"] = "left" }),
                              new FunctionCallContent(
                                  "conversation_2",
                                  tool.Name,
                                  new Dictionary<string, object?>(StringComparer.Ordinal) { ["what"] = "right" }),
                        ]);
                    yield break;
                }

                yield return new ChatResponseUpdate(ChatRole.Assistant, "looked up both.");
            }

            public async Task<ChatResponse> GetResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                CancellationToken cancellationToken = default)
            {
                List<ChatResponseUpdate> updates = [];
                await foreach (ChatResponseUpdate? update in GetStreamingResponseAsync(messages, options, cancellationToken)
                    .ConfigureAwait(false))
                {
                    updates.Add(update);
                }

                return updates.ToChatResponse();
            }

            public object? GetService(Type serviceType, object? serviceKey = null)
            {
                return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
            }

            public void Dispose()
            {
            }
        }
    }
}
