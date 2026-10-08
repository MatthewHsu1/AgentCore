using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Conversation.Commands;
using AgentCore.Application.Runtime.Session;
using AgentCore.Application.Runtime.Turn;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tools.Builtin
{
    /// <summary>The <c>uses: voice.plan</c> function: one <see cref="SetVoicePlanCommand"/> through the turn's channel.</summary>
    internal sealed class VoicePlanTool(ToolConfiguration tool)
    {
        /// <summary>Builds the function the model calls, named after the declaration.</summary>
        /// <returns>The function.</returns>
        public AIFunction AsAIFunction()
        {
            return AIFunctionFactory.Create(Plan, BuiltinToolOptions.Options(tool));
        }

        private JsonObject Plan(
            [Description("The plan for the voice, at most 1,000 characters: the next step, what to ask the caller, and when to delegate again.")] string plan,
            TurnInvocation? turn = null)
        {
            if (string.IsNullOrWhiteSpace(plan))
            {
                return Failed("plan is required: the next step for the voice.");
            }

            if (plan.Length > SetVoicePlanCommand.MaxLength)
            {
                return Failed($"The plan is {plan.Length} characters. The limit is {SetVoicePlanCommand.MaxLength}. Shorten it and call {tool.Id} again.");
            }

            if (turn is null)
            {
                return Failed("No conversation is running, so there is no voice to send the plan to.");
            }

            IChannelControl channel = turn.Channel ?? UnattachedChannel.Instance;
            return channel.Send(new SetVoicePlanCommand(plan)) switch
            {
                ChannelCommandResult.Scheduled => new JsonObject
                {
                    ["sent"] = true,
                    ["message"] = "Sent. The voice follows this plan until you send a new one.",
                },
                ChannelCommandResult.NotSupported => Failed("This conversation has no separate voice. The plan was not sent."),
                ChannelCommandResult.Ending => Failed("The conversation is ending. The plan was not sent."),
                _ => throw new UnreachableException($"A channel answered {nameof(SetVoicePlanCommand)} with an unknown result."),
            };
        }

        private JsonObject Failed(string message)
        {
            return ToolErrorResult.Create(tool.Id, message);
        }
    }
}
