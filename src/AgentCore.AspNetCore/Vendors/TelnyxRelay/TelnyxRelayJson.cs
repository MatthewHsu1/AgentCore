using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentCore.AspNetCore.Vendors.TelnyxRelay;

/// <summary>
/// The one serializer setting of the relay wire.
/// </summary>
/// <remarks>
/// <see cref="JsonSerializerDefaults.Web"/> gives camelCase names and case-insensitive reads,
/// which is exactly what the vendor sends and expects. One shared instance avoids the per-call
/// options cache that a fresh instance would rebuild for every frame.
/// </remarks>
internal static class TelnyxRelayJson
{
    /// <summary>Gets the options every read and every write of this wire uses.</summary>
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
