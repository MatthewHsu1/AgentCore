using AgentCore.Application.Ports;
using AgentCore.Application.State;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AgentCore.AspNetCore.Sessions;

/// <summary>
/// Re-reads one <c>vocabulary:</c> slot's domain on its own <c>refreshSeconds</c> interval, for as
/// long as the host is up.
/// </summary>
/// <remarks>
/// One instance per slot, following <see cref="CallSessionSweeper"/>'s <see cref="PeriodicTimer"/>
/// idiom. Section 11: a refresh that fails, or that succeeds but is degenerate (K46), keeps the
/// cache's last good view and logs — <see cref="VocabularyCache.Replace"/> already leaves the slot
/// unchanged when it throws, so this type only has to keep the loop alive and report what happened.
/// </remarks>
/// <param name="slot">The slot this instance refreshes.</param>
/// <param name="path">The resolved payload path, already through <c>scope.template</c>.</param>
/// <param name="maxValues">The slot's <c>vocabulary.maxValues</c>, passed as the read's own limit.</param>
/// <param name="wildcardValue">The scope's wildcard sentinel to strip (K6), or <see langword="null"/>.</param>
/// <param name="refreshSeconds">The slot's <c>vocabulary.refreshSeconds</c>. Always greater than zero.</param>
/// <param name="port">The facet read.</param>
/// <param name="cache">The cache this instance's reads are installed into.</param>
/// <param name="timeProvider">Where the <see cref="PeriodicTimer"/> ticks from.</param>
/// <param name="logger">Where a failed or degenerate refresh is reported.</param>
internal sealed class VocabularyRefreshService(
    string slot,
    string path,
    int maxValues,
    string? wildcardValue,
    int refreshSeconds,
    IFacetVocabularyPort port,
    VocabularyCache cache,
    TimeProvider timeProvider,
    ILogger<VocabularyRefreshService> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(refreshSeconds), timeProvider);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await RefreshAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>Runs one refresh: a read and an attempted install.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <remarks>
    /// Exposed <see langword="internal"/> so a test can drive one tick directly, without fighting the
    /// <see cref="PeriodicTimer"/>.
    /// </remarks>
    internal async ValueTask RefreshAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<string> values;
        try
        {
            values = await port.ReadAsync(path, maxValues, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception fault) when (fault is not OperationCanceledException)
        {
            VocabularyRefreshServiceLog.RefreshFailed(logger, slot, fault);
            return;
        }

        try
        {
            cache.Replace(slot, values, maxValues, wildcardValue);
        }
        catch (VocabularyException degenerate)
        {
            VocabularyRefreshServiceLog.RefreshDegenerate(logger, slot, degenerate);
        }
    }
}

/// <summary>Every line a <see cref="VocabularyRefreshService"/> writes.</summary>
internal static partial class VocabularyRefreshServiceLog
{
    /// <summary>A refresh's own read threw. The last good vocabulary is kept.</summary>
    /// <param name="logger">The service's own logger.</param>
    /// <param name="slot">The slot the refresh was for.</param>
    /// <param name="exception">The cause.</param>
    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "the vocabulary refresh for slot '{Slot}' failed. The last good vocabulary is kept.")]
    public static partial void RefreshFailed(ILogger logger, string slot, Exception exception);

    /// <summary>A refresh's read succeeded but failed one of section 10's four boot tests (K46).</summary>
    /// <param name="logger">The service's own logger.</param>
    /// <param name="slot">The slot the refresh was for.</param>
    /// <param name="exception">The <see cref="VocabularyException"/> naming which test failed.</param>
    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Warning,
        Message = "the vocabulary refresh for slot '{Slot}' returned a degenerate read. The last good "
            + "vocabulary is kept.")]
    public static partial void RefreshDegenerate(ILogger logger, string slot, Exception exception);
}
