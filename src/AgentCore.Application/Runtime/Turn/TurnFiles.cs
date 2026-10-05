using AgentCore.Application.Transcript;

namespace AgentCore.Application.Runtime.Turn
{
    /// <summary>What a turn has published to the caller's file store and not yet attached to a message.</summary>
    /// <param name="turn">The turn-level collector a graph participant's collector reports what it delivered to, or <see langword="null"/>.</param>
    internal sealed class TurnFiles(TurnFiles? turn = null) : TurnAttachments<FileContent>
    {
        private readonly List<FileContent> _delivered = [];

        /// <summary>Gets what reached the caller as cards, by this collector and every participant collector filed under it, in delivery order.</summary>
        internal IReadOnlyList<FileContent> Delivered
        {
            get
            {
                lock (_delivered)
                {
                    return [.. _delivered];
                }
            }
        }

        /// <summary>Records a file delivered to the caller, so the turn's stored words keep its card.</summary>
        /// <param name="file">The file the store kept.</param>
        internal void Deliver(FileContent file)
        {
            ArgumentNullException.ThrowIfNull(file);

            lock (_delivered)
            {
                int index = _delivered.FindIndex(known => string.Equals(known.Name, file.Name, StringComparison.Ordinal));
                if (index >= 0)
                {
                    _delivered[index] = file;
                }
                else
                {
                    _delivered.Add(file);
                }
            }

            turn?.Deliver(file);
        }

        /// <summary>Files one published file under the outermost tool call of the publishing flow, or drops it outside one.</summary>
        /// <param name="file">The file the store kept.</param>
        /// <param name="callId">The outermost tool call the publishing flow runs inside, or <see langword="null"/>.</param>
        public void Publish(FileContent file, string? callId)
        {
            ArgumentNullException.ThrowIfNull(file);

            if (callId is null)
            {
                return;
            }

            // The same name published twice is one blob: the store replaced the bytes.
            Attach(callId, file, existing => string.Equals(existing.Name, file.Name, StringComparison.Ordinal));
        }
    }
}
