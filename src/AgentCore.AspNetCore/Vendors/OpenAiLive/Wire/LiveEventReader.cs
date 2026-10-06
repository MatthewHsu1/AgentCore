using System.Text.Json;
using AgentCore.Application.Hooks.Notices;

namespace AgentCore.AspNetCore.Vendors.OpenAiLive.Wire
{
    /// <summary>Reads one sideband message. It never throws: a message it cannot use becomes <see cref="LiveEvent.Other"/>.</summary>
    internal static class LiveEventReader
    {
        internal static LiveEvent Read(string json)
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(json);
            }
            catch (JsonException)
            {
                return new LiveEvent.Other("unparseable");
            }

            using JsonDocument parsed = document;
            JsonElement root = parsed.RootElement;
            string type = Text(root, "type") ?? string.Empty;

            return type switch
            {
                OpenAiLiveEvents.Started => new LiveEvent.Started(),
                OpenAiLiveEvents.InputTranscriptDelta => Transcript(root, Speaker.Caller, type),
                OpenAiLiveEvents.OutputTranscriptDelta => Transcript(root, Speaker.Agent, type),
                OpenAiLiveEvents.DelegationCreated => root.TryGetProperty("delegation", out JsonElement delegation) && Text(delegation, "id") is { Length: > 0 } id
                    ? new LiveEvent.Delegation(id, Number(root, "offset_ms"))
                    : new LiveEvent.Other(type),
                OpenAiLiveEvents.CommentaryAppended => new LiveEvent.Appended(Text(root, "client_event_id")),
                OpenAiLiveEvents.Closed => new LiveEvent.Closed(Text(root, "reason")),
                OpenAiLiveWire.ErrorEvent => root.TryGetProperty("error", out JsonElement error) && error.ValueKind == JsonValueKind.Object
                    ? new LiveEvent.Failed(Text(error, "code"), Text(error, "message"))
                    : new LiveEvent.Failed(Code: null, Message: null),
                _ => new LiveEvent.Other(type),
            };
        }

        private static LiveEvent Transcript(JsonElement root, Speaker speaker, string type)
        {
            return Text(root, "delta") is { } delta && Number(root, "start_ms") is { } start && Number(root, "end_ms") is { } end
                ? new LiveEvent.Transcript(speaker, delta, start, end)
                : new LiveEvent.Other(type);
        }

        private static string? Text(JsonElement element, string name)
        {
            return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }

        private static int? Number(JsonElement element, string name)
        {
            return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value)
                && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number)
                && number is >= int.MinValue and <= int.MaxValue
                ? (int)number
                : null;
        }
    }
}
