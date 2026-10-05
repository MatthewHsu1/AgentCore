using System.Text.Json;

namespace AgentCore.AspNetCore.Vendors.TelnyxRelay.Wire
{
    /// <summary>
    /// Reads one inbound relay frame, and never throws on what the vendor sent.
    /// </summary>
    internal static class TelnyxRelayFrameReader
    {
        /// <summary>Reads one frame from the bytes of one WebSocket message.</summary>
        /// <param name="utf8">The whole message, already reassembled.</param>
        /// <param name="frame">The frame, or <see langword="null"/> when this build did not read one.</param>
        /// <param name="unknownType">
        /// The <c>type</c> value that no case matched, or <see langword="null"/> when the type was one
        /// this build models or the bytes carried no readable type at all.
        /// </param>
        /// <param name="refusedType">
        /// The <c>type</c> value whose body would not bind, or <see langword="null"/> when the body bound
        /// or there was no readable type. A caller tells this apart from <paramref name="unknownType"/>
        /// because the two need different answers: an unmodelled type is a frame from a later version of
        /// the vendor, and a refused body is a field of a frame this build does know.
        /// </param>
        /// <returns><see langword="true"/> when <paramref name="frame"/> holds a frame.</returns>
        public static bool TryRead(
            ReadOnlySpan<byte> utf8,
            out RelayFrame? frame,
            out string? unknownType,
            out string? refusedType)
        {
            frame = null;
            unknownType = null;
            refusedType = null;

            JsonDocument document;
            try
            {
                // A vendor payload never throws across this seam.
                Utf8JsonReader reader = new(utf8);
                document = JsonDocument.ParseValue(ref reader);
            }
            catch (JsonException)
            {
                return false;
            }

            using (document)
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object
                    || !document.RootElement.TryGetProperty("type", out JsonElement type)
                    || type.ValueKind != JsonValueKind.String)
                {
                    return false;
                }

                string name = type.GetString()!;
                try
                {
                    frame = name switch
                    {
                        "setup" => document.Deserialize<RelayFrame.Setup>(TelnyxRelayJson.Options),
                        "prompt" => document.Deserialize<RelayFrame.Prompt>(TelnyxRelayJson.Options),
                        "interrupt" => document.Deserialize<RelayFrame.Interrupt>(TelnyxRelayJson.Options),
                        "dtmf" => document.Deserialize<RelayFrame.Dtmf>(TelnyxRelayJson.Options),
                        "error" => document.Deserialize<RelayFrame.Error>(TelnyxRelayJson.Options),
                        _ => null,
                    };
                }
                catch (JsonException)
                {
                    // The type is one this build knows, so the bytes are readable and only a field is
                    // wrong. That is a refused frame, never a refused conversation.
                    refusedType = name;
                    return false;
                }

                if (frame is null)
                {
                    unknownType = name;
                    return false;
                }

                return true;
            }
        }
    }
}
