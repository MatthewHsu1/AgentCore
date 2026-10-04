using System.Text;
using AgentCore.Application.Hooks.Notices;
using AgentCore.AspNetCore.Vendors.OpenAiLive.Wire;

namespace AgentCore.AspNetCore.Vendors.OpenAiLive.Call
{
    /// <summary>
    /// Collects transcript deltas into lines, and splits the lines since the last delegation into one ask. A delegation
    /// closes the open line by arrival order, never by timestamp: <c>offset_ms</c> is the start of the last word, so a
    /// timestamp split moves that word into the next ask (13 of 13 test calls split cleanly by arrival order). Not thread-safe: one read loop owns it.
    /// </summary>
    internal sealed class LiveTranscriptLedger
    {
        private readonly List<LiveLine> _sinceDelegation = [];

        private readonly StringBuilder _line = new();

        private Speaker? _speaker;

        private int _start;

        private int _end;

        private bool _delegated;

        internal IReadOnlyList<LiveLine> Add(LiveEvent.Transcript delta)
        {
            ArgumentNullException.ThrowIfNull(delta);

            List<LiveLine> closed = [];
            if (_speaker is { } open && open != delta.Speaker)
            {
                Close(closed);
            }

            if (_speaker is null)
            {
                _speaker = delta.Speaker;
                _start = delta.StartMs;
            }

            _ = _line.Append(delta.Delta);
            _end = delta.EndMs;
            return closed;
        }

        /// <summary>Closes the open line and takes the ask of one delegation.</summary>
        /// <returns>
        /// The line closed now, the lines that ride ahead of the ask's words, and the words: empty when the caller said
        /// nothing since the last delegation.
        /// </returns>
        internal (IReadOnlyList<LiveLine> Closed, IReadOnlyList<LiveLine> Before, string CallerWords) TakeForDelegation()
        {
            List<LiveLine> closed = [];
            Close(closed);

            List<LiveLine> lines = [.. _delegated ? _sinceDelegation.SkipWhile(static line => line.Speaker == Speaker.Agent) : _sinceDelegation];
            _sinceDelegation.Clear();
            _delegated = true;

            int last = lines.FindLastIndex(static line => line.Speaker == Speaker.Caller);
            if (last < 0)
            {
                return (closed, [], string.Empty);
            }

            int first = last;
            while (first > 0 && lines[first - 1].Speaker == Speaker.Caller)
            {
                first--;
            }

            string words = string.Join(' ', lines[first..(last + 1)].Select(static line => line.Text));
            return (closed, lines[..first], words);
        }

        internal IReadOnlyList<LiveLine> Flush()
        {
            List<LiveLine> closed = [];
            Close(closed);
            return closed;
        }

        private void Close(List<LiveLine> closed)
        {
            if (_speaker is { } speaker && _line.ToString().Trim() is { Length: > 0 } text)
            {
                LiveLine line = new(speaker, text, _start, _end);
                closed.Add(line);
                _sinceDelegation.Add(line);
            }

            _speaker = null;
            _ = _line.Clear();
        }
    }
}
