using AgentCore.AspNetCore.DependencyInjection;
using AgentCore.Application.Ports;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentCore.AspNetCore.Conversation
{
    /// <summary>
    /// Runs the retention sweep of response ids for as long as the host is up.
    /// </summary>
    public sealed class ConversationSweeper(
        IServiceProvider services,
        IOptions<AgentCoreOptions> options,
        TimeProvider timeProvider,
        ILogger<ConversationSweeper> logger) : BackgroundService
    {
        private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

        private const int BatchSize = 500;

        /// <inheritdoc />
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            TimeSpan? retention = options.Value.ResponseRetention;

            if (retention is null)
            {
                return;
            }

            IConversations conversations = services.GetRequiredService<IConversations>();

            using PeriodicTimer timer = new(Interval, timeProvider);

            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    int swept = await conversations
                        .SweepAsync(retention.Value, BatchSize, stoppingToken)
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
        /// <param name="count">How many response ids the pass deleted.</param>
        [LoggerMessage(
            EventId = 1,
            Level = LogLevel.Debug,
            Message = "the retention sweep deleted {Count} response id(s).")]
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
