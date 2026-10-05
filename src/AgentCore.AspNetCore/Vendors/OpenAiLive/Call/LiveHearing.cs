using AgentCore.AspNetCore.Calls;
using AgentCore.AspNetCore.Vendors.OpenAiLive.Wire;

namespace AgentCore.AspNetCore.Vendors.OpenAiLive.Call
{
    /// <summary>
    /// Turns GPT-Live's transcript deltas into closed lines and raises each through the call core. Only the read loop
    /// calls it, so the ledger needs no lock.
    /// </summary>
    internal sealed class LiveHearing(PhoneCall call)
    {
        private readonly LiveTranscriptLedger _ledger = new();

        internal ValueTask HearAsync(LiveEvent.Transcript delta) => RaiseAsync(_ledger.Add(delta));

        internal ValueTask FlushAsync() => RaiseAsync(_ledger.Flush());

        /// <summary>Raises the lines the delegation closed, and gives the ask its context and the caller's new words.</summary>
        internal async ValueTask<(IReadOnlyList<LiveLine> Before, string Words)> TakeForDelegationAsync()
        {
            (IReadOnlyList<LiveLine> closed, IReadOnlyList<LiveLine> before, string words) = _ledger.TakeForDelegation();
            await RaiseAsync(closed).ConfigureAwait(false);
            return (before, words);
        }

        // Line times are offsets from the call's start on GPT-Live's audio clock, so they are placed on the call's own start.
        private async ValueTask RaiseAsync(IReadOnlyList<LiveLine> lines)
        {
            foreach (LiveLine line in lines)
            {
                await call.HeardAsync(
                    line.Speaker,
                    line.Text,
                    call.StartedAt.AddMilliseconds(line.StartMs),
                    call.StartedAt.AddMilliseconds(line.EndMs),
                    call.Asks.AnsweredTurn).ConfigureAwait(false);
            }
        }
    }
}
