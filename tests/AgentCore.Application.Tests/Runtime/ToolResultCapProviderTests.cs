using AgentCore.Application.Runtime.Compaction;
using AgentCore.Application.Tests.Transcript;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Runtime;

/// <summary>
/// <see cref="ToolResultCapProvider"/>: once the trigger fires, every tool result older than the
/// kept turns is cut to the cap, one message for one, with its id; nothing else moves.
/// </summary>
#pragma warning disable MAAI001 // The context constructors and triggers are the framework's own experimental surface.
public sealed class ToolResultCapProviderTests
{
    [Fact]
    public async Task UnderTheTrigger_TheListComesBackAsItWas()
    {
        var provider = new ToolResultCapProvider(CompactionTriggers.Never, keepTurns: 0, maxResultChars: 3);
        var input = new AIContext { Messages = Turns(2) };

        var output = await provider.InvokingAsync(Context(input), TestContext.Current.CancellationToken);

        Assert.Same(input, output);
    }

    [Fact]
    public async Task OverTheTrigger_OldResultsAreCutInPlaceWithTheirIds_TheKeptTurnsAreNot()
    {
        var provider = new ToolResultCapProvider(CompactionTriggers.Always, keepTurns: 1, maxResultChars: 3);
        var messages = Turns(3);
        var input = new AIContext { Messages = messages };

        var output = (await provider.InvokingAsync(Context(input), TestContext.Current.CancellationToken)).Messages!.ToList();

        Assert.Equal(["res…", "res…", "result-2"], output.Where(message => message.Role == ChatRole.Tool).Select(Result));
        Assert.Equal(messages.Select(message => message.MessageId), output.Select(message => message.MessageId));
        for (var index = 0; index < messages.Count; index++)
        {
            if (messages[index].Role != ChatRole.Tool)
            {
                Assert.Same(messages[index], output[index]);
            }
        }
    }

    private static string Result(ChatMessage message)
        => Assert.Single(message.Contents.OfType<FunctionResultContent>()).Result!.ToString()!;

    /// <summary>Turns of a question, a call, its result <c>result-N</c>, and a reply; four messages each.</summary>
    private static List<ChatMessage> Turns(int count)
    {
        List<ChatMessage> messages = [];
        for (var turn = 0; turn < count; turn++)
        {
            messages.Add(new ChatMessage(ChatRole.User, $"q{turn}") { MessageId = $"m-{turn}-q" });
            messages.Add(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent($"call-{turn}", "tool")]) { MessageId = $"m-{turn}-call" });
            messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent($"call-{turn}", $"result-{turn}")]) { MessageId = $"m-{turn}-result" });
            messages.Add(new ChatMessage(ChatRole.Assistant, $"a{turn}") { MessageId = $"m-{turn}-a" });
        }

        return messages;
    }

    private static AIContextProvider.InvokingContext Context(AIContext input)
        => new(StubAgent.Instance, new StubSession(), input);
}
#pragma warning restore MAAI001
