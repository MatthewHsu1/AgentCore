using System.Text.Json.Nodes;

namespace AgentCore.AspNetCore.Tests.Vendors.OpenAiLive
{
    /// <summary>
    /// Recorded GPT-Live event logs, as the server sent them.
    /// </summary>
    internal static class LiveLog
    {
        /// <summary>Reads the events the server sent, in arrival order, as raw JSON.</summary>
        public static IReadOnlyList<string> Inbound(string fixture)
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Vendors", "OpenAiLive", "Fixtures", fixture + ".jsonl");
            return [.. File.ReadLines(path)
                .Where(line => line.Length > 0)
                .Select(line => JsonNode.Parse(line)!)
                .Where(entry => (string?)entry["dir"] == "in")
                .Select(entry => entry["event"]!.ToJsonString())];
        }

        /// <summary>Finds the <paramref name="nth"/> event of one type.</summary>
        public static int IndexOf(IReadOnlyList<string> events, string type, int nth = 1)
        {
            int seen = 0;
            for (int index = 0; index < events.Count; index++)
            {
                if ((string?)JsonNode.Parse(events[index])!["type"] == type && ++seen == nth)
                {
                    return index;
                }
            }

            throw new InvalidOperationException($"The log holds fewer than {nth} '{type}' events.");
        }
    }
}
