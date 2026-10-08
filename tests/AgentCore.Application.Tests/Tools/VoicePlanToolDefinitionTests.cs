using System.Text.Json.Nodes;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Conversation.Commands;
using AgentCore.Application.Runtime.Turn;
using AgentCore.Application.Tools;
using AgentCore.Application.Tools.Builtin;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Tools
{
    /// <summary>
    /// The <c>uses: voice.plan</c> builtin: it checks the plan once, sends it through the turn's channel, and tells the
    /// model whether the voice got it. It returns an error result and never throws.
    /// </summary>
    public sealed class VoicePlanToolDefinitionTests
    {
        private static readonly ToolConfiguration Declared =
            new() { Id = "plan", Kind = ToolKind.Builtin, Uses = BuiltinToolNames.VoicePlan, Description = "Set the voice's plan." };

        [Fact]
        public void TheModelSeesPlanAsItsOneRequiredInput()
        {
            AIFunction function = (AIFunction)new VoicePlanToolDefinition().Build(Declared, new BuiltinToolPorts(ChatClients: null));

            Assert.Equal(["plan"], function.JsonSchema.GetProperty("properties").EnumerateObject().Select(property => property.Name));
            Assert.Equal(["plan"], function.JsonSchema.GetProperty("required").EnumerateArray().Select(name => name.GetString()));
        }

        [Fact]
        public async Task AMissingPlanIsItsOwnErrorResultNotAThrow()
        {
            RecordingChannel channel = new(ChannelCommandResult.Scheduled);

            JsonObject result = await PlanAsync(plan: null, channel);

            Assert.Equal("plan is required: the next step for the voice.", Message(result));
            Assert.Empty(channel.Seen);
        }

        [Fact]
        public async Task APlanTheChannelTakesIsSentWhole()
        {
            RecordingChannel channel = new(ChannelCommandResult.Scheduled);

            JsonObject result = await PlanAsync("Next: ask for the serial number.", channel);

            Assert.True((bool?)result["sent"]);
            SetVoicePlanCommand sent = Assert.IsType<SetVoicePlanCommand>(Assert.Single(channel.Seen));
            Assert.Equal("Next: ask for the serial number.", sent.Text);
        }

        [Fact]
        public async Task APlanAtTheLimitIsSent()
        {
            RecordingChannel channel = new(ChannelCommandResult.Scheduled);

            JsonObject result = await PlanAsync(new string('a', SetVoicePlanCommand.MaxLength), channel);

            Assert.True((bool?)result["sent"]);
        }

        [Fact]
        public async Task APlanOverTheLimitTellsTheModelToShortenItAndSendsNothing()
        {
            RecordingChannel channel = new(ChannelCommandResult.Scheduled);

            JsonObject result = await PlanAsync(new string('a', 1820), channel);

            Assert.Equal("The plan is 1820 characters. The limit is 1000. Shorten it and call plan again.", Message(result));
            Assert.Empty(channel.Seen);
        }

        [Fact]
        public async Task AWhiteSpacePlanIsAnErrorResultNotAThrow()
        {
            RecordingChannel channel = new(ChannelCommandResult.Scheduled);

            JsonObject result = await PlanAsync("   ", channel);

            Assert.Equal("plan is required: the next step for the voice.", Message(result));
            Assert.Empty(channel.Seen);
        }

        [Fact]
        public async Task AChannelWithNoVoiceSaysThePlanWasNotSent()
        {
            JsonObject result = await PlanAsync("Next: ask for the model.", new RecordingChannel(ChannelCommandResult.NotSupported));

            Assert.Equal("This conversation has no separate voice. The plan was not sent.", Message(result));
        }

        [Fact]
        public async Task AnEndingConversationSaysThePlanWasNotSent()
        {
            JsonObject result = await PlanAsync("Next: ask for the model.", new RecordingChannel(ChannelCommandResult.Ending));

            Assert.Equal("The conversation is ending. The plan was not sent.", Message(result));
        }

        [Fact]
        public async Task ATurnWithNoChannelSaysThereIsNoSeparateVoice()
        {
            JsonObject result = await PlanAsync("Next: ask for the model.", channel: null);

            Assert.Equal("This conversation has no separate voice. The plan was not sent.", Message(result));
        }

        [Fact]
        public async Task NoConversationRunningSaysThereIsNoVoice()
        {
            JsonObject result = await PlanAsync("Next: ask for the model.", channel: null, turn: false);

            Assert.Equal("No conversation is running, so there is no voice to send the plan to.", Message(result));
        }

        private static string? Message(JsonObject result)
        {
            Assert.True((bool?)result[ToolErrorResult.ErrorProperty]);
            return (string?)result[ToolErrorResult.MessageProperty];
        }

        private static async Task<JsonObject> PlanAsync(string? plan, IChannelControl? channel, bool turn = true)
        {
            AIFunction function = (AIFunction)new VoicePlanToolDefinition().Build(Declared, new BuiltinToolPorts(ChatClients: null));
            AIFunctionArguments arguments = [];
            if (plan is not null)
            {
                arguments["plan"] = plan;
            }

            if (turn)
            {
                _ = new TurnInvocation { ConversationId = "conversation-1", TurnIndex = 0, Stage = string.Empty, Channel = channel }
                    .FileIn(arguments);
            }

            object? result = await function.InvokeAsync(arguments, TestContext.Current.CancellationToken);

            return Assert.IsType<JsonObject>(ToolResultJson.ToNode(result));
        }

        private sealed class RecordingChannel(ChannelCommandResult answer) : IChannelControl
        {
            public List<ChannelCommand> Seen { get; } = [];

            public ChannelCommandResult Send(ChannelCommand command)
            {
                Seen.Add(command);
                return answer;
            }
        }
    }
}
