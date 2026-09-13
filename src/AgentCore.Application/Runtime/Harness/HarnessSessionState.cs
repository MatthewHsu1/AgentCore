using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Agents.AI;

namespace AgentCore.Application.Runtime.Harness;

/// <summary>
/// Moves the harness providers' MAF state — <c>todos:</c>, <c>mode:</c>, <c>memory:</c>,
/// <c>files:</c>, <c>background:</c> — and the approval queue and standing rules between a live
/// <see cref="AgentSession"/>'s state bag and <see cref="Calls.CallSessionState.Providers"/>.
/// </summary>
internal static class HarnessSessionState
{
    private static readonly IReadOnlyDictionary<string, JsonElement> Empty =
        ReadOnlyDictionary<string, JsonElement>.Empty;

    /// <summary>
    /// Reads the harness providers' state out of <paramref name="session"/>, keeping only the keys
    /// <paramref name="keys"/> names.
    /// </summary>
    /// <param name="session">The live session, or <see langword="null"/>.</param>
    /// <param name="keys">The harness providers' state keys this document declares.</param>
    /// <returns>
    /// The kept keys, each value cloned off the bag's own serialization. Empty for a
    /// <see langword="null"/> session or an empty <paramref name="keys"/> — the bag is never
    /// serialized in that case, because it also holds the transcript.
    /// </returns>
    public static IReadOnlyDictionary<string, JsonElement> Capture(AgentSession? session, IReadOnlySet<string> keys)
    {
        if (session is null || keys.Count == 0)
        {
            return Empty;
        }

        var serialized = session.StateBag.Serialize();
        Dictionary<string, JsonElement> providers = new(StringComparer.Ordinal);

        foreach (var property in serialized.EnumerateObject())
        {
            if (keys.Contains(property.Name))
            {
                providers[property.Name] = property.Value.Clone();
            }
        }

        return providers.Count == 0 ? Empty : providers;
    }

    /// <summary>
    /// Builds the envelope <see cref="AIAgent.DeserializeSessionAsync"/> expects: <c>{ "stateBag":
    /// { key: element, ... } }</c>, with no other member.
    /// </summary>
    public static JsonElement Wrap(IReadOnlyDictionary<string, JsonElement> providers)
    {
        JsonObject stateBag = [];

        foreach (var (key, element) in providers)
        {
            stateBag[key] = JsonSerializer.SerializeToNode(element);
        }

        JsonObject envelope = new() { ["stateBag"] = stateBag };

        return JsonSerializer.SerializeToElement(envelope);
    }
}
