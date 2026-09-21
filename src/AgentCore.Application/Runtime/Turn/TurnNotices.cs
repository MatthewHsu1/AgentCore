using System.Threading.Channels;

namespace AgentCore.Application.Runtime.Turn
{
    /// <summary>
    /// One turn's out-of-band channel to its own streaming consumer.
    /// </summary>
    internal sealed class TurnNotices
    {
        private readonly Channel<NoticeContent> _channel = Channel.CreateUnbounded<NoticeContent>(
            new UnboundedChannelOptions { SingleReader = true });

        /// <summary>Gets the reader side, read only by the merge that owns this turn.</summary>
        internal ChannelReader<NoticeContent> Reader => _channel.Reader;

        /// <summary>Posts one notice. Silently dropped once <see cref="Complete"/> has run.</summary>
        internal void Post(NoticeContent content)
        {
            ArgumentNullException.ThrowIfNull(content);

            _ = _channel.Writer.TryWrite(content);
        }

        /// <summary>Completes the channel. The merge calls this once, when the turn's stream ends.</summary>
        internal void Complete()
        {
            _ = _channel.Writer.TryComplete();
        }
    }
}
