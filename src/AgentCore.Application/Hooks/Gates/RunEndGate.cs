namespace AgentCore.Application.Hooks.Gates
{
    /// <summary>
    /// A run has a reply, before the turn is sealed. A hook may ask the agent to run again. It does not fire for a
    /// graph entry, for a nested run, for the run that reaches the loop cap, for a run that ends on a pending approval
    /// request, or for a run where an earlier <c>loop: until:</c> evaluator already asked to continue.
    /// </summary>
    public sealed class RunEndGate : HookGate
    {
        internal RunEndGate(
            HookScope scope,
            string? agentId,
            string replyText,
            int iteration,
            IDictionary<string, object?> items)
            : base(scope)
        {
            ArgumentNullException.ThrowIfNull(replyText);
            ArgumentNullException.ThrowIfNull(items);

            AgentId = agentId;
            ReplyText = replyText;
            Iteration = iteration;
            Items = items;
        }

        /// <summary>Gets the agent that ran, when known.</summary>
        public string? AgentId { get; }

        /// <summary>Gets the run's reply text.</summary>
        public string ReplyText { get; }

        /// <summary>Gets how many runs of this turn have completed: 1 after the first run.</summary>
        public int Iteration { get; }

        /// <summary>Gets the turn's shared state.</summary>
        public IDictionary<string, object?> Items { get; }

        internal string? Continuation { get; private set; }

        /// <summary>
        /// Runs the agent again with <paramref name="message"/>. The next run reads the conversation, this turn's user
        /// message and earlier runs, then the message. The message is not a user row, and the caller never sees it. Runs
        /// are capped by the agent's <c>loop: maxRounds:</c>; a <c>loop:</c> block without <c>maxRounds:</c>
        /// caps at the framework's default of 10, and an agent with no <c>loop:</c> at 3. Terminal.
        /// </summary>
        /// <param name="message">What the agent is told for the next run.</param>
        public void Continue(string message)
        {
            ArgumentNullException.ThrowIfNull(message);
            Stage(terminal: true, () => Continuation = message);
        }
    }
}
