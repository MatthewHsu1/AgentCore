using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Configuration.Compilation;

internal static class GraphStateCarrier
{
    /// <summary>The message key the snapshot rides under.</summary>
    internal const string ArgumentsKey = "urn:agentcore:graph-state";

    /// <summary>Builds the carrier heading one run's request messages.</summary>
    /// <param name="snapshot">Every declared slot and the three reserved slots, read at run start.</param>
    /// <returns>A content-free message only the graph-state entry reads.</returns>
    internal static ChatMessage Build(IReadOnlyDictionary<string, JsonNode?> snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new ChatMessage(ChatRole.System, string.Empty)
        {
            AdditionalProperties = new AdditionalPropertiesDictionary { [ArgumentsKey] = snapshot },
        };
    }

    /// <summary>Takes the snapshot off the head of one run's request, when it carries one.</summary>
    /// <param name="messages">The run's accumulated messages, oldest first.</param>
    /// <param name="snapshot">The snapshot, without the carrier around it.</param>
    /// <returns>Whether a carrier headed the messages. The carrier is removed either way it answers.</returns>
    internal static bool TryTake(List<ChatMessage> messages, out IReadOnlyDictionary<string, JsonNode?>? snapshot)
    {
        ArgumentNullException.ThrowIfNull(messages);

        snapshot = null;
        if (messages.Count == 0
            || messages[0].AdditionalProperties?.TryGetValue(ArgumentsKey, out var filed) != true
            || filed is not IReadOnlyDictionary<string, JsonNode?> carried)
        {
            return false;
        }

        messages.RemoveAt(0);
        snapshot = carried;
        return true;
    }
}
