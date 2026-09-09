using AgentCore.Application.Configuration.Compilation;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Runtime.Compaction;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentCore.Application.Tests.Runtime;

/// <summary>
/// <see cref="CompactionStrategyFactory"/>: resolved settings to a MAF strategy, and the trimming
/// the 2026-09-09 handoff recorded as unproven.
/// </summary>
#pragma warning disable MAAI001 // Compaction is evaluation-only in Microsoft.Agents.AI 1.17.0.
public sealed class CompactionStrategyFactoryTests
{
    [Theory]
    [InlineData(CompactionStrategyKind.ToolResult, typeof(ToolResultCompactionStrategy))]
    [InlineData(CompactionStrategyKind.Truncate, typeof(TruncationCompactionStrategy))]
    [InlineData(CompactionStrategyKind.SlidingWindow, typeof(SlidingWindowCompactionStrategy))]
    [InlineData(CompactionStrategyKind.ContextWindow, typeof(PipelineCompactionStrategy))]
    public void Create_EachKind_BuildsItsStrategy(CompactionStrategyKind kind, Type expected)
    {
        Assert.IsType(expected, CompactionStrategyFactory.Create(Resolved(kind, messages: 6)));
    }

    [Fact]
    public async Task Create_Truncate_RemovesOldestAndKeepsNewest()
    {
        var history = History(12);

        var compacted = (await CompactionProvider.CompactAsync(
            CompactionStrategyFactory.Create(Resolved(CompactionStrategyKind.Truncate, messages: 6)),
            history,
            cancellationToken: TestContext.Current.CancellationToken)).ToList();

        Assert.True(compacted.Count < history.Count, $"kept all {history.Count} messages");
        Assert.Equal(history[^1].Text, compacted[^1].Text);
    }

    [Fact]
    public async Task Create_ToolResult_ShrinksTheText()
    {
        var history = HistoryWithToolResults(6, resultChars: 4000);
        var before = history.Sum(Size);

        var compacted = (await CompactionProvider.CompactAsync(
            CompactionStrategyFactory.Create(Resolved(CompactionStrategyKind.ToolResult, messages: 8)),
            history,
            cancellationToken: TestContext.Current.CancellationToken)).ToList();

        var after = compacted.Sum(Size);

        // MAF's own formatter grows this number. The capping formatter is what makes it fall.
        Assert.True(after < before / 2, $"the fold did not cap: {before} -> {after}");
    }

    [Fact]
    public async Task Create_SameWordsTwiceFromEmptyState_Agrees()
    {
        // The claim the resume design rests on: a resumed call builds a new session, so the provider
        // starts empty and re-derives. If two passes disagreed, a resumed call would not match the
        // call it resumed, and compaction state would have to be persisted.
        var history = HistoryWithToolResults(6, resultChars: 4000);
        var settings = Resolved(CompactionStrategyKind.ToolResult, messages: 8);

        var first = (await CompactionProvider.CompactAsync(
            CompactionStrategyFactory.Create(settings), history, cancellationToken: TestContext.Current.CancellationToken)).ToList();
        var second = (await CompactionProvider.CompactAsync(
            CompactionStrategyFactory.Create(settings), history, cancellationToken: TestContext.Current.CancellationToken)).ToList();

        Assert.Equal(first.Count, second.Count);
        Assert.Equal(first.Select(message => message.Text), second.Select(message => message.Text));
    }

    private static int Size(ChatMessage message) => message.Text.Length
        + message.Contents.OfType<FunctionResultContent>().Sum(c => c.Result?.ToString()?.Length ?? 0);

    private static ResolvedCompaction Resolved(CompactionStrategyKind kind, int messages) => new(
        kind,
        new CompactionTriggerConfiguration { Messages = messages },
        Keep: 2,
        FoldResultChars: 200);

    private static List<ChatMessage> History(int count)
    {
        List<ChatMessage> messages = [];
        for (var index = 0; index < count / 2; index++)
        {
            messages.Add(new ChatMessage(ChatRole.User, $"question {index}"));
            messages.Add(new ChatMessage(ChatRole.Assistant, $"answer {index}"));
        }

        return messages;
    }

    private static List<ChatMessage> HistoryWithToolResults(int turns, int resultChars)
    {
        List<ChatMessage> messages = [];
        for (var index = 0; index < turns; index++)
        {
            messages.Add(new ChatMessage(ChatRole.User, $"question {index}"));
            messages.Add(new ChatMessage(ChatRole.Assistant,
                [new FunctionCallContent($"call-{index}", "lookup", new Dictionary<string, object?>())]));
            messages.Add(new ChatMessage(ChatRole.Tool,
                [new FunctionResultContent($"call-{index}", new string('x', resultChars))]));
            messages.Add(new ChatMessage(ChatRole.Assistant, $"answer {index}"));
        }

        return messages;
    }
}
#pragma warning restore MAAI001
