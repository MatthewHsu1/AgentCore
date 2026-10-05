namespace AgentCore.Application.Hooks.Gates
{
    /// <summary>A tool returned. A hook may replace its result or end the loop after it.</summary>
    public sealed class ToolResultGate : HookGate
    {
        internal ToolResultGate(
            HookScope scope,
            string toolName,
            string callId,
            object? result,
            IDictionary<string, object?> items)
            : base(scope)
        {
            ArgumentNullException.ThrowIfNull(toolName);
            ArgumentNullException.ThrowIfNull(callId);
            ArgumentNullException.ThrowIfNull(items);

            ToolName = toolName;
            CallId = callId;
            Result = result;
            Items = items;
        }

        /// <summary>Gets the name the model called.</summary>
        public string ToolName { get; }

        /// <summary>Gets the id the model gave this call.</summary>
        public string CallId { get; }

        /// <summary>Gets the tool's result, as the hook before this one left it.</summary>
        public object? Result { get; }

        /// <summary>Gets the turn's shared state.</summary>
        public IDictionary<string, object?> Items { get; }

        internal bool Replaced { get; private set; }

        internal object? NewResult { get; private set; }

        internal bool EndsLoop { get; private set; }

        /// <summary>Tells the model <paramref name="result"/> instead of what the tool returned.</summary>
        /// <param name="result">The result the model reads.</param>
        public void ReplaceResult(object? result)
        {
            Stage(terminal: false, () =>
            {
                Replaced = true;
                NewResult = result;
            });
        }

        /// <summary>Ends the tool loop after this call: the model is not asked again this run.</summary>
        public void EndLoop()
        {
            Stage(terminal: false, () => EndsLoop = true);
        }
    }
}
