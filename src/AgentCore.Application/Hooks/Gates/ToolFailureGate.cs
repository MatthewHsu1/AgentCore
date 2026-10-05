namespace AgentCore.Application.Hooks.Gates
{
    /// <summary>A tool threw. A hook may retry the call or answer for it; with no decision AgentCore's own policy applies.</summary>
    public sealed class ToolFailureGate : HookGate
    {
        internal ToolFailureGate(
            HookScope scope,
            string toolName,
            string callId,
            int attempt,
            Exception exception,
            bool canRetry,
            IDictionary<string, object?> items)
            : base(scope)
        {
            ArgumentNullException.ThrowIfNull(toolName);
            ArgumentNullException.ThrowIfNull(callId);
            ArgumentNullException.ThrowIfNull(exception);
            ArgumentNullException.ThrowIfNull(items);

            ToolName = toolName;
            CallId = callId;
            Attempt = attempt;
            Exception = exception;
            CanRetry = canRetry;
            Items = items;
        }

        /// <summary>Gets the name the model called.</summary>
        public string ToolName { get; }

        /// <summary>Gets the id the model gave this call.</summary>
        public string CallId { get; }

        /// <summary>Gets how many retries this call already had.</summary>
        public int Attempt { get; }

        /// <summary>Gets what the tool threw.</summary>
        public Exception Exception { get; }

        /// <summary>Gets whether <see cref="Retry"/> is allowed: false on the last attempt the retry cap allows.</summary>
        public bool CanRetry { get; }

        /// <summary>Gets the turn's shared state.</summary>
        public IDictionary<string, object?> Items { get; }

        internal bool Retrying { get; private set; }

        internal bool Responded { get; private set; }

        internal object? Result { get; private set; }

        /// <summary>Calls the tool again. The model sees the second result. Terminal.</summary>
        /// <exception cref="InvalidOperationException"><see cref="CanRetry"/> is false.</exception>
        public void Retry()
        {
            if (!CanRetry)
            {
                throw new InvalidOperationException("This call reached the retry cap, so it cannot be retried.");
            }

            Stage(terminal: true, () => Retrying = true);
        }

        /// <summary>Answers for the tool with <paramref name="result"/> instead of AgentCore's own failure policy. Terminal.</summary>
        /// <param name="result">The result the model reads.</param>
        public void Respond(object? result)
        {
            Stage(terminal: true, () =>
            {
                Responded = true;
                Result = result;
            });
        }
    }
}
