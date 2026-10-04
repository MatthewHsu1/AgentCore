using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentCore.AspNetCore.Vendors.TelnyxRelay.Wire
{
    /// <summary>
    /// The one serializer setting of the relay wire.
    /// </summary>
    internal static class TelnyxRelayJson
    {
        /// <summary>Gets the options every read and every write of this wire uses.</summary>
        public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
    }
}
