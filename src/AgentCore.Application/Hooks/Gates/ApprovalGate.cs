namespace AgentCore.Application.Hooks.Gates
{
    /// <summary>A tool that needs approval was called. A hook may approve or deny it; with no decision a human is asked.</summary>
    public sealed class ApprovalGate : HookGate
    {
        internal ApprovalGate(
            HookScope scope,
            string toolName,
            string callId,
            IReadOnlyDictionary<string, object?> arguments,
            IDictionary<string, object?> items)
            : base(scope)
        {
            ArgumentNullException.ThrowIfNull(toolName);
            ArgumentNullException.ThrowIfNull(callId);
            ArgumentNullException.ThrowIfNull(arguments);
            ArgumentNullException.ThrowIfNull(items);

            ToolName = toolName;
            CallId = callId;
            Arguments = arguments;
            Items = items;
        }

        /// <summary>Gets the name the model called.</summary>
        public string ToolName { get; }

        /// <summary>Gets the id the model gave this call.</summary>
        public string CallId { get; }

        /// <summary>Gets the arguments the model sent.</summary>
        public IReadOnlyDictionary<string, object?> Arguments { get; }

        /// <summary>Gets the turn's shared state.</summary>
        public IDictionary<string, object?> Items { get; }

        internal bool? Approved { get; private set; }

        internal string? DenyReason { get; private set; }

        /// <summary>Lets the tool run without asking a human. Terminal.</summary>
        public void Approve()
        {
            Stage(terminal: true, () => Approved = true);
        }

        /// <summary>Refuses the call without asking a human. The model is told <paramref name="reason"/>. Terminal.</summary>
        /// <param name="reason">Why the call was refused.</param>
        public void Deny(string reason)
        {
            ArgumentNullException.ThrowIfNull(reason);
            Stage(terminal: true, () =>
            {
                Approved = false;
                DenyReason = reason;
            });
        }
    }
}
