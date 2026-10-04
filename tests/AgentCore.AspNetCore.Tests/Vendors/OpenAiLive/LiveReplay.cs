using AgentCore.AspNetCore.Vendors.OpenAiLive.Call;
using AgentCore.AspNetCore.Vendors.OpenAiLive.Wire;

namespace AgentCore.AspNetCore.Tests.Vendors.OpenAiLive
{
    /// <summary>
    /// A recorded log replayed through the reader and the ledger: its asks, the lines said ahead of each ask that it
    /// writes before its words, its closed lines, and why it closed.
    /// </summary>
    internal sealed record LiveReplay(List<(string Id, string Words)> Asks, List<IReadOnlyList<LiveLine>> Before, List<LiveLine> Lines, string? ClosedReason)
    {
        public static LiveReplay Of(string fixture)
        {
            LiveTranscriptLedger ledger = new();
            List<(string, string)> asks = [];
            List<IReadOnlyList<LiveLine>> before = [];
            List<LiveLine> lines = [];
            string? closed = null;
            foreach (string json in LiveLog.Inbound(fixture))
            {
                switch (LiveEventReader.Read(json))
                {
                    case LiveEvent.Transcript delta:
                        lines.AddRange(ledger.Add(delta));
                        break;
                    case LiveEvent.Delegation delegation:
                        (IReadOnlyList<LiveLine> closedLines, IReadOnlyList<LiveLine> said, string words) = ledger.TakeForDelegation();
                        lines.AddRange(closedLines);
                        asks.Add((delegation.Id, words));
                        before.Add(said);
                        break;
                    case LiveEvent.Closed end:
                        lines.AddRange(ledger.Flush());
                        closed = end.Reason;
                        break;
                    default:
                        break;
                }
            }

            return new LiveReplay(asks, before, lines, closed);
        }
    }
}
