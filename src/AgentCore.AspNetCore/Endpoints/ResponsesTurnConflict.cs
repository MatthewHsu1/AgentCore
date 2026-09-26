using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Runtime;
using Microsoft.AspNetCore.Http;

namespace AgentCore.AspNetCore.Endpoints
{
    /// <summary>
    /// How the Responses path answers a turn it refused: another request held the conversation past the wait limit, or
    /// saved the same turn of the conversation first.
    /// </summary>
    internal static class ResponsesTurnConflict
    {
        /// <summary>The error code of the answer, streamed or not.</summary>
        public const string Code = "turn_conflict";

        /// <summary>What the caller reads when another request saved the same turn first, after this turn ran.</summary>
        public const string ConflictMessage =
            "another request saved this turn of the conversation first, so this turn's reply was not saved. Reload the "
            + "conversation and send the message again.";

        /// <summary>What the caller reads when another request still held the conversation, so this turn never ran.</summary>
        public const string BusyMessage =
            "another request is still running a turn of this conversation, so this turn was refused before it ran. Wait "
            + "for that turn to finish and send the message again.";

        /// <summary>Picks the message for a <see cref="TurnRefusals"/> reason.</summary>
        /// <param name="refusedReason">The <c>refusedReason</c> of the turn's <c>turn.refused</c> event.</param>
        /// <returns>The message the caller reads.</returns>
        public static string MessageFor(string refusedReason) =>
            refusedReason == TurnRefusals.Busy ? BusyMessage : ConflictMessage;

        /// <summary>
        /// Ends a stream whose headers already left with the Responses <c>error</c> event, in place of the
        /// <c>response.completed</c> the turn never reached.
        /// </summary>
        /// <param name="http">The request being answered.</param>
        /// <param name="lastFrame">The last frame the stream wrote, or <see langword="null"/> when it wrote none.</param>
        /// <param name="cancellationToken">Cancels the write.</param>
        public static async Task WriteEventAsync(HttpContext http, string? lastFrame, CancellationToken cancellationToken)
        {
            JsonObject error = new()
            {
                ["type"] = "error",
                ["code"] = Code,
                ["message"] = ConflictMessage,
                ["param"] = null,
                ["sequence_number"] = NextSequence(lastFrame),
            };

            await http.Response.WriteAsync(
                    string.Create(CultureInfo.InvariantCulture, $"event: error\ndata: {error.ToJsonString()}\n\n"),
                    cancellationToken)
                .ConfigureAwait(false);
            await http.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Reads the sequence number that follows the last frame's, or 0 when there was none.</summary>
        private static int NextSequence(string? lastFrame)
        {
            int data = lastFrame?.IndexOf("data: ", StringComparison.Ordinal) ?? -1;
            if (data < 0)
            {
                return 0;
            }

            using JsonDocument frame = JsonDocument.Parse(lastFrame![(data + "data: ".Length)..]);
            return frame.RootElement.TryGetProperty("sequence_number", out JsonElement sequence)
                && sequence.TryGetInt32(out int number)
                    ? number + 1
                    : 0;
        }
    }
}
