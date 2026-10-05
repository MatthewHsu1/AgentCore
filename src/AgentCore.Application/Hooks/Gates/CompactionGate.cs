namespace AgentCore.Application.Hooks.Gates
{
    /// <summary>AgentCore is about to compact. A hook may cancel the compaction or supply its summary.</summary>
    public sealed class CompactionGate : HookGate
    {
        internal CompactionGate(HookScope scope, int messageCount, IDictionary<string, object?> items)
            : base(scope)
        {
            ArgumentNullException.ThrowIfNull(items);

            MessageCount = messageCount;
            Items = items;
        }

        /// <summary>Gets how many messages the compaction may fold: the stored messages before the newest turn.</summary>
        public int MessageCount { get; }

        /// <summary>Gets the turn's shared state.</summary>
        public IDictionary<string, object?> Items { get; }

        internal bool Cancelled { get; private set; }

        internal string? Summary { get; private set; }

        /// <summary>Skips this compaction: the conversation keeps the view it had. Terminal.</summary>
        public void Cancel()
        {
            Stage(terminal: true, () => Cancelled = true);
        }

        /// <summary>Uses <paramref name="text"/> as the summary, and the summarising model is not called. Terminal.</summary>
        /// <param name="text">The summary.</param>
        public void ReplaceSummary(string text)
        {
            ArgumentNullException.ThrowIfNull(text);
            Stage(terminal: true, () => Summary = text);
        }
    }
}
