using AgentCore.Application.Transcript;
using System.Runtime.CompilerServices;
using AgentCore.Application.Conversation;
using Microsoft.Extensions.AI;
using AgentCore.Application.Blobs;

namespace AgentCore.AspNetCore.Endpoints
{
    /// <summary>
    /// The files one turn published, linked for the browser once the turn is over.
    /// </summary>
    internal sealed class TurnStreamFiles
    {
        private readonly List<FileContent> _files = [];

        /// <summary>Notes every published file one update carries.</summary>
        /// <param name="update">One update of the stream.</param>
        internal void Note(ChatResponseUpdate update)
        {
            _files.AddRange(update.Contents.OfType<FileContent>());
        }

        /// <summary>Links every noted file the store kept, in the order they were noted.</summary>
        /// <param name="conversations">The door to the stored conversation.</param>
        /// <param name="conversationId">The conversation that owns the files.</param>
        /// <param name="cancellationToken">Cancels the lookups.</param>
        /// <returns>One part for each file the store holds.</returns>
        internal async IAsyncEnumerable<TurnStreamFile> ResolveAsync(
            Conversations conversations,
            string conversationId,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (_files.Count == 0)
            {
                yield break;
            }

            IReadOnlyList<FileLink> links = await conversations
                .LinkFilesAsync(conversationId, [new ChatMessage(ChatRole.Assistant, [.. _files])], cancellationToken)
                .ConfigureAwait(false);

            foreach ((BlobRef? blob, Uri? url) in links)
            {
                yield return new TurnStreamFile(new FilePayload
                {
                    Name = blob.Name,
                    Title = _files.LastOrDefault(file => string.Equals(file.Name, blob.Name, StringComparison.Ordinal))?.Title,
                    MediaType = blob.MediaType,
                    Length = blob.Length,
                    Url = url?.ToString(),
                });
            }
        }
    }
}
