using AgentCore.Application.Ports;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentCore.AspNetCore.Conversation
{
    /// <summary>
    /// Runs the retention sweep of <see cref="IConversations"/> for as long as the host is up.
    /// </summary>
    /// <remarks>
    /// AgentCore never registers this on its own: a host that wants old conversations erased opts in by
    /// calling <c>AddConversationSweep</c>. Without that call, conversations are kept forever.
    /// </remarks>
    public sealed class ConversationSweeper(
        IConversations conversations,
        IOptions<ConversationSweepOptions> options,
        TimeProvider timeProvider,
        ILogger<ConversationSweeper> logger) : BackgroundService
    {
        /// <inheritdoc />
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            ConversationSweepOptions settings = options.Value;

            using PeriodicTimer timer = new(settings.Interval, timeProvider);

            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    int swept = await conversations
                        .SweepAsync(settings.Retention, settings.BatchSize, stoppingToken)
                        .ConfigureAwait(false);

                    ConversationSweeperLog.Swept(logger, swept);
                }
                catch (Exception fault) when (fault is not OperationCanceledException)
                {
                    ConversationSweeperLog.SweepFaulted(logger, fault);
                }
            }
        }
    }

    /// <summary>Every line the retention sweep writes.</summary>
    internal static partial class ConversationSweeperLog
    {
        /// <summary>One sweep pass finished.</summary>
        /// <param name="logger">The logger of the sweeper.</param>
        /// <param name="count">How many conversations the pass erased.</param>
        [LoggerMessage(
            EventId = 1,
            Level = LogLevel.Debug,
            Message = "the retention sweep erased {Count} conversation(s).")]
        public static partial void Swept(ILogger logger, int count);

        /// <summary>A sweep pass could not finish.</summary>
        /// <param name="logger">The logger of the sweeper.</param>
        /// <param name="exception">The cause.</param>
        [LoggerMessage(
            EventId = 2,
            Level = LogLevel.Error,
            Message = "the retention sweep faulted, so this pass erased nothing. It will retry on the next tick.")]
        public static partial void SweepFaulted(ILogger logger, Exception exception);
    }
}
