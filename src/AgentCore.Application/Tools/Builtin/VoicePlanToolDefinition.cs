using AgentCore.Application.Configuration.Schema;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tools.Builtin
{
    /// <summary>The <c>uses: voice.plan</c> builtin: one <see cref="VoicePlanTool"/>. It needs no port.</summary>
    internal sealed class VoicePlanToolDefinition : IBuiltinToolDefinition
    {
        /// <inheritdoc />
        public string Name => BuiltinToolNames.VoicePlan;

        /// <inheritdoc />
        public string DefaultDescription =>
            "Sets the plan the voice runs between your answers: the next step, what to ask the caller or watch for, "
            + "and when to delegate to you again. A new plan replaces the last one. "
            + "Words for the caller go in your reply, never in the plan.";

        /// <inheritdoc />
        public AITool Build(ToolConfiguration tool, BuiltinToolPorts ports)
        {
            ArgumentNullException.ThrowIfNull(tool);
            ArgumentNullException.ThrowIfNull(ports);

            return new VoicePlanTool(tool).AsAIFunction();
        }
    }
}
