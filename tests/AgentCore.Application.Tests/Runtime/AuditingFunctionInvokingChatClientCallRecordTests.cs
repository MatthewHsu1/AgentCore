using AgentCore.Application.Runtime;
using AgentCore.Application.Tests.Fakes;
using AgentCore.Application.Tools.Binding;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Runtime;

/// <summary>
/// The call record the model wrote is the model's alone. The turn a tool reads arrives beside it,
/// through <see cref="AIFunctionArguments.Context"/>, never among its keys — so what a checkpoint
/// persists is exactly what the model said, and a live turn is never asked to survive JSON.
/// </summary>
public sealed class AuditingFunctionInvokingChatClientCallRecordTests
{
    [Fact]
    public async Task TheTurnReachesTheTool_AndTheModelsCallRecordKeepsOnlyItsOwnKeys()
    {
        TurnInvocation? seen = null;

        var tool = AIFunctionFactory.Create(
            (string city, TurnInvocation? turn) =>
            {
                seen = turn;
                return $"sunny in {city}";
            },
            new AIFunctionFactoryOptions
            {
                Name = "weather",
                ConfigureParameterBinding = ToolParameterBindings.For,
            });

        TurnInvocation invocation = new() { ConversationId = "conversation", TurnIndex = 0, Stage = "" };

        ToolCallingChatClient inner = new(
            "the loop continues.",
            new Dictionary<string, object?>(StringComparer.Ordinal) { ["city"] = "Taipei" });
        using AuditingFunctionInvokingChatClient client = new(inner);

        var response = await client.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "weather")],
            new ChatOptions { Tools = [tool], AdditionalProperties = new AdditionalPropertiesDictionary { [TurnInvocation.ArgumentsKey] = invocation } },
            TestContext.Current.CancellationToken);

        Assert.Equal("conversation", seen?.ConversationId);

        var call = response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>().Single();
        Assert.Equal(["city"], call.Arguments!.Keys);
        Assert.Equal("Taipei", call.Arguments["city"]);
    }
}
