using Microsoft.Extensions.AI;

namespace AgentCore.Application.Runtime.ToolCalls
{
    /// <summary>One tool call <see cref="ConversationToolRuns"/> counts while it runs.</summary>
    /// <param name="call">The call the model made.</param>
    internal sealed class ToolRun(FunctionCallContent call)
    {
        /// <summary>Gets the call the model made.</summary>
        internal FunctionCallContent Call { get; } = call;

        /// <summary>Gets the call as it runs. It ends with the tool's result, or with its fault.</summary>
        internal Task<object?> Running { get; set; } = Task.FromResult<object?>(null);

        /// <summary>Gets or sets whether the call ended. Guarded by the lock of its <see cref="ConversationToolRuns"/>.</summary>
        internal bool Done { get; set; }

        /// <summary>
        /// Gets or sets what reads the call's citations and files once it finished, set when its turn stopped waiting for
        /// it. Guarded by the lock of its <see cref="ConversationToolRuns"/>.
        /// </summary>
        internal Func<IEnumerable<AIContent>>? Attachments { get; set; }
    }
}
