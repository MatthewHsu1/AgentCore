using System.Text.Json;
using AgentCore.Domain.Knowledge;

namespace AgentCore.Application.Knowledge;

/// <summary>
/// Writes a list of cards to JSON and reads it back with the same payload shape the store gave.
/// </summary>
internal static class KnowledgeCardCodec
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Encode(IReadOnlyList<KnowledgeCard> cards)
        => JsonSerializer.Serialize(cards, Options);

    public static IReadOnlyList<KnowledgeCard> Decode(string json)
    {
        var cards = JsonSerializer.Deserialize<List<KnowledgeCard>>(json, Options) ?? [];

        for (var index = 0; index < cards.Count; index++)
        {
            cards[index] = cards[index] with { Extras = RebuildMap(cards[index].Extras) };
        }

        return cards;
    }

    private static Dictionary<string, object?> RebuildMap(IReadOnlyDictionary<string, object?> map)
        => map.ToDictionary(entry => entry.Key, entry => Rebuild(entry.Value), StringComparer.Ordinal);

    private static object? Rebuild(object? value) => value switch
    {
        JsonElement element => Rebuild(element),
        IReadOnlyDictionary<string, object?> map => RebuildMap(map),
        _ => value,
    };

    /// <summary>Mirrors the shape <c>QdrantPointConverter</c> produces: long, double, bool, string, list, map.</summary>
    private static object? Rebuild(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        // Boxed on each arm: a bare long/double conditional promotes the long to a double.
        JsonValueKind.Number => element.TryGetInt64(out var whole) ? (object)whole : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Array => (IReadOnlyList<object?>)[.. element.EnumerateArray().Select(Rebuild)],
        JsonValueKind.Object => element
            .EnumerateObject()
            .ToDictionary(property => property.Name, property => Rebuild(property.Value), StringComparer.Ordinal),
        _ => null,
    };
}
