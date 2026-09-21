using AgentCore.TestSupport;
using AgentCore.Application.Tools.Binding;
using AgentCore.Application.Tools.Registry;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Secrets;
using AgentCore.Application.Tools.Builtin;
using AgentCore.Infrastructure.Secrets;
using AgentCore.Infrastructure.Tests.Tools;
using AgentCore.Infrastructure.Tools;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Infrastructure.Tests
{
    /// <summary>
    /// The whole seam, from the document to the conversation: secrets, then tools, then the compile table.
    /// </summary>
    /// <remarks>
    /// The worked example of section 8.1 declares all four tool kinds. This test walks the three that
    /// need a source, in the order a host starts them: resolve every <c>${secret:name}</c> once, build
    /// the sources over the resolved values, then compile.
    /// </remarks>
    public sealed class ToolSeamTests
    {
        private const string Yaml =
            """
          apiVersion: agentcore/v1
          tools:
            - id: lookup_order
              kind: http
              description: Read one order by its identifier.
              parameters:
                type: object
                properties: { orderId: { type: string } }
                required: [ orderId ]
              request:
                method: GET
                url: "https://api.example.com/orders/{orderId}"
                headers: { Authorization: "Bearer ${secret:orders-api-key}" }
            - id: create_case
              kind: binding
              binds: CreateCase
              description: Open a service case for a human agent.
              parameters:
                type: object
                properties: { summary: { type: string } }
                required: [ summary ]
          agents:
            items:
              - { id: identifier, instructions: "<stage delta>", tools: [ lookup_order ] }
              - { id: resolver,   instructions: "<stage delta>", tools: [] }
              - { id: escalator,  instructions: "<stage delta>", tools: [ create_case ] }
          entries:
            main:
              policy:
                initial: identify
                stages:
                  - { id: identify, agent: identifier, to: [ { stage: resolve } ] }
                  - { id: resolve,  agent: resolver,   to: [ { stage: escalate } ] }
                  - { id: escalate, agent: escalator,  terminal: true }
          providers:
              conversation:   { kind: telnyx-relay }
              speech:
                stt: { kind: telnyx-relay }
                tts: { kind: telnyx-relay }
          """;

        private static CancellationToken Token => TestContext.Current.CancellationToken;

        [Fact]
        public async Task TheWorkedExample_CompilesWithEveryToolBound()
        {
            AgentCoreConfiguration document = ConfigurationLoader.LoadYaml(Yaml);

            // Step one: resolve every reference the document holds, once.
            ChainedSecretResolver chain = new([new EnvironmentSecretResolver(_ => "sk-live-0123456789")]);
            ResolvedSecrets secrets = await ResolvedSecrets.ResolveAsync(document, chain, Token);

            using StubHttpMessageHandler handler = StubHttpMessageHandler.Answering(HttpStatusCode.OK, /*lang=json,strict*/ """{"status":"shipped"}""");
            using HttpClient client = new(handler);

            ToolBindingRegistry bindings = new();
            _ = bindings.Register("CreateCase", (arguments, cancellationToken)
                => ValueTask.FromResult<object?>(JsonNode.Parse("""{"caseId":"C-1"}""")));

            // Step two: build the sources over the resolved values.
            ToolRegistry registry = await ToolRegistryBuilder.BuildAsync(
                [
                    new BuiltinToolSource(new BuiltinToolPorts(null)),
                      new HttpToolSource(client, secrets),
                      new BindingToolSource(bindings),
                  ],
                new ToolSourceContext(document), Token);

            // Step three: compile.
            using OneReplyChatClient model = new("done");
            CompiledAgent compiled = ConfigurationCompiler.CompileAll(
                document,
                new AgentCompilationContext(new RecordingChatClientFactory(model)) { Tools = registry })["main"];

            Assert.Equal(3, compiled.Agents.Count);

            // The HTTP tool now makes its call. Before this seam existed it could not.
            AIFunction lookup = Assert.IsType<AIFunction>(registry.Resolve("lookup_order"), exactMatch: false);

            object? result = await lookup.InvokeAsync(
                new AIFunctionArguments(new Dictionary<string, object?>(StringComparer.Ordinal) { ["orderId"] = "A-42" }),
                Token);

            Assert.Equal("shipped", Assert.IsType<JsonObject>(result)["status"]!.GetValue<string>());
            Assert.Equal(
                "Bearer sk-live-0123456789",
                handler.Requests[0].Headers.GetValues("Authorization").Single());
        }

        /// <summary>An offline model that answers one line and calls no tool.</summary>
        private sealed class OneReplyChatClient(string reply) : IChatClient
        {
            private readonly string _reply = reply;

            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                await Task.Yield();
                yield return new ChatResponseUpdate(ChatRole.Assistant, _reply);
            }

            public Task<ChatResponse> GetResponseAsync(
                IEnumerable<ChatMessage> messages,
                ChatOptions? options = null,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _reply)));
            }

            public object? GetService(Type serviceType, object? serviceKey = null)
            {
                ArgumentNullException.ThrowIfNull(serviceType);
                return serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
            }

            public void Dispose()
            {
                // Nothing to release.
            }
        }
    }
}
