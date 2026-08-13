using System.Text.Json;

namespace AgentCore.AspNetCore.Vendors.TelnyxRelay;

/// <summary>
/// Reads one inbound relay frame, and never throws on what the vendor sent.
/// </summary>
/// <remarks>
/// <para>
/// The switch below is written by hand on purpose. System.Text.Json polymorphic deserialization
/// cannot express the rule of section 7.1, because its ignore option reverts to the contract of
/// the base type and <see cref="RelayFrame"/> is abstract. It also wants the discriminator first,
/// and nothing in the vendor contract promises that.
/// </para>
/// <para>
/// A vendor that adds a frame must not be able to drop a call, so an unmodelled type is refused
/// and named rather than thrown. The caller logs the name once for the call, not once for the
/// frame, per section 3.1.
/// </para>
/// <para>
/// A vendor that <b>changes</b> a frame must not be able to drop one either, and section 7.1 draws
/// no line between the two. A known <c>type</c> whose body will not bind is therefore a third
/// answer, apart from both "here is your frame" and "these bytes are not readable at all": the
/// frame is refused, the socket lives, and the call goes on. Two shapes reach it from the live
/// wire — a decimal <c>durationUntilInterruptMs</c>, which would otherwise end a call at the exact
/// moment of a barge-in, and a non-string value inside <c>customParameters</c>, which would
/// otherwise end it on the <c>setup</c> frame before it began.
/// </para>
/// </remarks>
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
    /// <remarks>
    /// A <see langword="false"/> answer with both names <see langword="null"/> is the only
    /// unreadable case: the bytes are not JSON, not an object, or carry no string <c>type</c>. That
    /// is the one the caller may close the socket over.
    /// </remarks>
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
            var reader = new Utf8JsonReader(utf8);
            document = JsonDocument.ParseValue(ref reader);
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var name = type.GetString()!;
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
                // wrong. Section 7.1: that is a refused frame, never a refused call.
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
