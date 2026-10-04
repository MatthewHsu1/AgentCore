namespace AgentCore.Application.Hooks.Gates
{
    /// <summary>A tool is about to run. A hook may block it, answer for it, change its arguments, or end the loop after it.</summary>
    public sealed class ToolGate : HookGate
    {
        internal ToolGate(
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

        /// <summary>Gets the arguments, as the model sent them or as the hook before this one replaced them.</summary>
        public IReadOnlyDictionary<string, object?> Arguments { get; }

        /// <summary>Gets the turn's shared state. Tools read the same dictionary through <c>ToolCallScope.Items</c>.</summary>
        public IDictionary<string, object?> Items { get; }

        internal bool Blocked { get; private set; }

        internal bool Responded { get; private set; }

        internal object? Result { get; private set; }

        internal IReadOnlyDictionary<string, object?>? NewArguments { get; private set; }

        internal bool EndsLoop { get; private set; }

        /// <summary>Does not run the tool. The model reads <paramref name="resultForModel"/> as the tool's answer. Terminal.</summary>
        /// <param name="resultForModel">What the model is told, for example why the call was refused.</param>
        public void Block(object? resultForModel)
        {
            Stage(terminal: true, () =>
            {
                Blocked = true;
                Result = resultForModel;
            });
        }

        /// <summary>Does not run the tool, and answers for it with <paramref name="result"/>. Terminal.</summary>
        /// <param name="result">The tool's answer.</param>
        public void Respond(object? result)
        {
            Stage(terminal: true, () =>
            {
                Responded = true;
                Result = result;
            });
        }

        /// <summary>Runs the tool with these arguments. The model's own call record keeps what the model sent.</summary>
        /// <param name="arguments">The arguments the tool gets. A key left out is removed.</param>
        public void ReplaceArguments(IReadOnlyDictionary<string, object?> arguments)
        {
            ArgumentNullException.ThrowIfNull(arguments);
            Stage(terminal: false, () => NewArguments = arguments);
        }

        /// <summary>Ends the tool loop after this call: the model is not asked again this run.</summary>
        public void EndLoop()
        {
            Stage(terminal: false, () => EndsLoop = true);
        }
    }
}
