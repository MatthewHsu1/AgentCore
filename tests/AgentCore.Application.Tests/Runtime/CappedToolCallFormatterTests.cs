using AgentCore.Application.Runtime.Compaction;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Runtime;

/// <summary>
/// <see cref="CappedToolCallFormatter"/>: what a folded tool-call group carries forward. MAF's own
/// formatter copies the whole result, which grows the conversation it was asked to shrink.
/// </summary>
#pragma warning disable MAAI001 // CompactionMessageGroup and CompactionMessageIndex are the framework's own experimental surface.
public sealed class CappedToolCallFormatterTests
{
    [Fact]
    public void Create_LongResult_IsCutToTheCap()
    {
        var written = CappedToolCallFormatter.Create(50)(Group("lookup", new string('x', 4000)));

        Assert.Contains("lookup", written, StringComparison.Ordinal);
        Assert.True(written.Length < 200, $"the fold was {written.Length} characters, expected under 200");
    }

    [Fact]
    public void Create_LongResult_SaysItWasCut()
    {
        var written = CappedToolCallFormatter.Create(50)(Group("lookup", new string('x', 4000)));

        Assert.Contains("…", written, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_ShortResult_IsKeptWhole()
    {
        var written = CappedToolCallFormatter.Create(50)(Group("lookup", "sunny"));

        Assert.Contains("sunny", written, StringComparison.Ordinal);
        Assert.DoesNotContain("…", written, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_NoToolCalls_IsEmpty()
    {
        var group = Group(CompactionGroupKind.User, [new ChatMessage(ChatRole.User, "hello")]);

        Assert.Empty(CappedToolCallFormatter.Create(50)(group));
    }

    [Fact]
    public void Create_TwoResultsShareOneCallId_RendersTheLastOneWithoutThrowing()
    {
        var group = Group(
            CompactionGroupKind.ToolCall,
            [
                new ChatMessage(ChatRole.Assistant,
                    [new FunctionCallContent("call-1", "lookup", new Dictionary<string, object?>())]),
                new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", "first")]),
                new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", "last")]),
            ]);

        var written = CappedToolCallFormatter.Create(50)(group);

        Assert.Contains("last", written, StringComparison.Ordinal);
        Assert.DoesNotContain("first", written, StringComparison.Ordinal);
    }

    // CompactionMessageGroup has no public constructor: its only public members are read-only
    // properties, so a group has to come from the index that MAF's own strategies build it
    // through. Passing a null tokenizer is fine — AddGroup falls back to an estimate, and this
    // formatter never reads TokenCount.
    private static CompactionMessageGroup Group(CompactionGroupKind kind, IReadOnlyList<ChatMessage> messages) =>
        new CompactionMessageIndex([], null!).AddGroup(kind, messages, turnIndex: null);

    private static CompactionMessageGroup Group(string tool, string result) => Group(
        CompactionGroupKind.ToolCall,
        [
            new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent("call-1", tool, new Dictionary<string, object?>())]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", result)]),
        ]);
}
#pragma warning restore MAAI001
