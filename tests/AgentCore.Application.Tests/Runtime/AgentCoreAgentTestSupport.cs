using AgentCore.Application.Calls;
using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using AgentCore.Application.Ports;
using AgentCore.Application.Runtime;
using AgentCore.TestSupport;
using Microsoft.Extensions.AI;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentCore.Application.Tests.Runtime;

/// <summary>The documents and the shim-building helper the <see cref="AgentCoreAgent"/> tests share.</summary>
internal static class AgentCoreAgentTestSupport
{
    internal const string SingleAgentYaml =
        """
        apiVersion: agentcore/v1
        agents:
          defaults:
            model: { ref: reply }
          items:
            - { id: solo, instructions: "answer the caller" }
        entries:
          main:
            agent: solo
        """;

    // The same row with one declared slot, so the round trip has something of its own to carry.
    // SingleAgentYaml declares none, and a document with no slots proves nothing about slots.
    internal const string SlottedAgentYaml =
        """
        apiVersion: agentcore/v1
        state:
          escalate: { type: boolean, writer: extractor, default: false }
          note: { type: string, writer: extractor, default: "" }
        agents:
          defaults:
            model: { ref: reply }
          items:
            - { id: solo, instructions: "answer the caller" }
        entries:
          main:
            agent: solo
        """;

    // A policy that ends itself after one turn, so a call can be driven terminal and then round
    // tripped. The guard reads the reserved turnIndex slot, so no extractor and no tool is needed.
    internal const string TerminalAgentYaml =
        """
        apiVersion: agentcore/v1
        guards:
          always: { ">=": [ { var: turnIndex }, 0 ] }
        agents:
          defaults:
            model: { ref: reply }
          items:
            - { id: solo, instructions: "answer the caller" }
        entries:
          main:
            policy:
              initial: talking
              stages:
                - { id: talking, agent: solo, to: [ { stage: done, when: always } ] }
                - { id: done, agent: solo, terminal: true }
        """;

    internal static AgentCoreAgent BuildAgent(
        IChatClient reply,
        out CompiledAgent compiled,
        string yaml = SingleAgentYaml,
        ICallStore? store = null,
        string entryName = "main")
    {
        var document = ConfigurationLoader.LoadYaml(yaml);
        compiled = ConfigurationCompiler.CompileAll(
            document,
            new AgentCompilationContext(new RoutingChatClientFactory(reply)) { CallStore = store })[entryName];

        CallSessionFactory sessions = new(compiled, new GuardEvaluator(compiled.Configuration.Guards));
        return new AgentCoreAgent(sessions, entryName);
    }

    /// <summary>Writes what a host hands to <c>DeserializeSessionAsync</c>: a call id, its state, and its entry.</summary>
    internal static JsonElement Envelope(string callId, CallSessionState state, string entryName = "main")
        => JsonSerializer.SerializeToElement(
            new Dictionary<string, JsonNode?>(StringComparer.Ordinal)
            {
                ["callId"] = JsonValue.Create(callId),
                ["state"] = JsonSerializer.SerializeToNode(state, CallStateJson.Options),
                ["entry"] = JsonValue.Create(entryName),
            },
            CallStateJson.Options);
}
