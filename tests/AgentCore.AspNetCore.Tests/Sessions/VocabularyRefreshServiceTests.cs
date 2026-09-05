using AgentCore.Application.Ports;
using AgentCore.Application.State;
using AgentCore.AspNetCore.Sessions;
using AgentCore.TestSupport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentCore.AspNetCore.Tests.Sessions;

/// <summary>
/// Section 11's refresh rows: a failed or degenerate refresh keeps the cache's last good view and
/// logs, and the loop itself follows <c>CallSessionSweeper</c>'s <see cref="PeriodicTimer"/> idiom.
/// </summary>
public sealed class VocabularyRefreshServiceTests
{
    [Fact]
    public async Task RefreshAsync_TheReadThrows_KeepsTheLastGoodViewAndLogs()
    {
        VocabularyCache cache = new();
        cache.Replace("brand", ["acme", "globex"], maxValues: 10);
        var before = cache.Snapshot()["brand"];

        RecordingLoggerFactory loggers = new();
        FailingPort port = new(new InvalidOperationException("qdrant is down"));

        VocabularyRefreshService service = new(
            "brand", "facets.brand", 10, null, 900, port, cache,
            TimeProvider.System, loggers.CreateLogger<VocabularyRefreshService>());

        await service.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Same(before, cache.Snapshot()["brand"]);
        var line = Assert.Single(loggers.Of(1));
        Assert.Equal("brand", line.Field<string>("Slot"));
        Assert.Same(port.Failure, line.Exception);
    }

    [Fact]
    public async Task RefreshAsync_TheReadReturnsExactlyMaxValues_KeepsTheLastGoodViewAndLogsDegenerate()
    {
        // K46: a successful but truncated read must not overwrite the last good view.
        VocabularyCache cache = new();
        cache.Replace("brand", ["acme", "globex"], maxValues: 10);
        var before = cache.Snapshot()["brand"];

        RecordingLoggerFactory loggers = new();
        FakePort port = new(["a", "b", "c"]);

        VocabularyRefreshService service = new(
            "brand", "facets.brand", 3, null, 900, port, cache,
            TimeProvider.System, loggers.CreateLogger<VocabularyRefreshService>());

        await service.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Same(before, cache.Snapshot()["brand"]);
        var line = Assert.Single(loggers.Of(2));
        Assert.Equal("brand", line.Field<string>("Slot"));
        Assert.IsType<VocabularyException>(line.Exception);
    }

    [Fact]
    public async Task RefreshAsync_TheReadReturnsAGoodSet_ReplacesTheView()
    {
        VocabularyCache cache = new();
        cache.Replace("brand", ["acme"], maxValues: 10);

        FakePort port = new(["acme", "globex"]);
        VocabularyRefreshService service = new(
            "brand", "facets.brand", 10, null, 900, port, cache,
            TimeProvider.System, NullLogger<VocabularyRefreshService>.Instance);

        await service.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["acme", "globex"], cache.Snapshot()["brand"].Originals);
    }

    [Fact]
    public async Task ExecuteAsync_TheTimerTicks_RunsARefreshOnTheConfiguredInterval()
    {
        VocabularyCache cache = new();
        cache.Replace("brand", ["acme"], maxValues: 10);

        FakePort port = new(["acme", "globex"]);
        FakeTimeProvider time = new(DateTimeOffset.UnixEpoch);

        VocabularyRefreshService service = new(
            "brand", "facets.brand", 10, null, refreshSeconds: 60, port, cache,
            time, NullLogger<VocabularyRefreshService>.Instance);

        await service.StartAsync(TestContext.Current.CancellationToken);

        // BackgroundService.StartAsync runs ExecuteAsync through Task.Run, so the PeriodicTimer may
        // not exist yet when this returns. Retry the advance until the fake port reports the tick
        // landed, rather than assuming a single Advance lands on a timer that is not armed yet.
        for (var attempt = 0; attempt < 200 && !port.Read.Task.IsCompleted; attempt++)
        {
            time.Advance(TimeSpan.FromSeconds(60));
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        await port.Read.Task.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        // The cache write is asynchronous from the read that unblocked Read.Task above, so give the
        // continuation a chance to run before reading the cache back.
        for (var attempt = 0; attempt < 100 && cache.Snapshot()["brand"].Originals.Count != 2; attempt++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.Equal(["acme", "globex"], cache.Snapshot()["brand"].Originals);

        await service.StopAsync(TestContext.Current.CancellationToken);
    }

    private sealed class FakePort(IReadOnlyList<string> values) : IFacetVocabularyPort
    {
        public TaskCompletionSource Read { get; } = new();

        public ValueTask<IReadOnlyList<string>> ReadAsync(
            string path, int limit, CancellationToken cancellationToken = default)
        {
            Read.TrySetResult();
            return ValueTask.FromResult(values);
        }
    }

    private sealed class FailingPort(Exception failure) : IFacetVocabularyPort
    {
        public Exception Failure { get; } = failure;

        public ValueTask<IReadOnlyList<string>> ReadAsync(
            string path, int limit, CancellationToken cancellationToken = default)
            => throw Failure;
    }
}
